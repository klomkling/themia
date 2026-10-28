using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Themia.Storage.S3;

/// <summary>An <see cref="IStorageProvider"/> over any S3-compatible backend. Cloudflare R2 and MinIO
/// are supported by setting <see cref="S3StorageOptions.ServiceUrl"/> + <see cref="S3StorageOptions.ForcePathStyle"/>.</summary>
public sealed class S3StorageProvider : IStorageProvider, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly string bucket;
    private readonly bool publicOnly;
    private readonly string publicBucket;
    private readonly string publicBaseUrl;
    private readonly bool ownsClient;
    private readonly long maxSpooledUploadBytes;

    /// <summary>Creates the provider, building the S3 client from <paramref name="options"/>.</summary>
    /// <param name="options">The S3 options.</param>
    public S3StorageProvider(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.PublicOnly)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
        }

        bucket = options.BucketName;
        options.Validate();
        publicOnly = options.PublicOnly;
        publicBucket = options.PublicBucketName;
        publicBaseUrl = options.PublicBaseUrl;
        client = BuildClient(options);
        ownsClient = true;
        maxSpooledUploadBytes = options.MaxSpooledUploadBytes;
    }

    /// <summary>Creates the provider over an existing client (used by tests against MinIO).</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="bucketName">The bucket name.</param>
    public S3StorageProvider(IAmazonS3 client, string bucketName)
        : this(client, bucketName, null)
    {
    }

    internal S3StorageProvider(IAmazonS3 client, string bucketName, long? maxSpooledUploadBytes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        this.client = client;
        bucket = bucketName;
        publicBucket = string.Empty;
        publicBaseUrl = string.Empty;
        ownsClient = false;
        this.maxSpooledUploadBytes = maxSpooledUploadBytes ?? new S3StorageOptions().MaxSpooledUploadBytes;
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Themia.Storage.StorageKey.EnsureMatchesVisibility(key, options.Visibility);
        var (resolvedBucket, resolvedKey) = Resolve(key);
        var contentType = string.IsNullOrEmpty(options.ContentType) ? "application/octet-stream" : options.ContentType;
        var request = new PutObjectRequest
        {
            BucketName = resolvedBucket,
            Key = resolvedKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        FileStream? buffered = null;
        try
        {
            if (client.Config.ServiceURL is { Length: > 0 } serviceUrl)
            {
                // S3-compatible servers such as Cloudflare R2 reject aws-chunked streaming signatures
                // ("STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented"), so send one plain body. That needs a known
                // Content-Length, hence a stream that cannot seek is spooled to a temporary file first.
                request.UseChunkEncoding = false;
                request.DisablePayloadSigning = serviceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!content.CanSeek)
                {
                    buffered = await SpoolAsync(content, cancellationToken).ConfigureAwait(false);
                    request.InputStream = buffered;
                }
            }

            var response = await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            var length = request.InputStream.CanSeek ? request.InputStream.Length : 0;
            return new StorageObjectInfo(key, length, contentType, response.ETag);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>
    /// Copies <paramref name="content"/> to a temporary file the OS names and creates owner-only (no part of the path
    /// comes from the caller), deleted when the returned stream is disposed, and never longer than the configured limit.
    /// </summary>
    private async Task<FileStream> SpoolAsync(Stream content, CancellationToken cancellationToken)
    {
        var spool = new FileStream(
            Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (spool.Length + read > maxSpooledUploadBytes)
                {
                    throw new InvalidOperationException(
                        $"The upload is longer than the {maxSpooledUploadBytes}-byte limit for a stream that cannot seek (S3StorageOptions.MaxSpooledUploadBytes).");
                }

                await spool.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var (resolvedBucket, resolvedKey) = Resolve(key);
            var response = await client.GetObjectAsync(resolvedBucket, resolvedKey, cancellationToken).ConfigureAwait(false);
            return new StorageReadResult(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var (resolvedBucket, resolvedKey) = Resolve(key);
            await client.GetObjectMetadataAsync(resolvedBucket, resolvedKey, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var (resolvedBucket, resolvedKey) = Resolve(key);
            var response = await client.GetObjectMetadataAsync(resolvedBucket, resolvedKey, cancellationToken).ConfigureAwait(false);
            return new StorageObjectInfo(key, response.ContentLength, response.Headers.ContentType ?? "application/octet-stream", response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var (resolvedBucket, resolvedKey) = Resolve(key);
        return client.DeleteObjectAsync(resolvedBucket, resolvedKey, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        var (resolvedBucket, resolvedKey) = Resolve(key);
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = resolvedBucket,
            Key = resolvedKey,
            Verb = request.Operation == PresignedUrlOperation.Put ? HttpVerb.PUT : HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(request.Expiry),
            ContentType = request.Operation == PresignedUrlOperation.Put ? request.ContentType : null,
            Protocol = PresignProtocol,
        }).ConfigureAwait(false);
        return new Uri(url);
    }

    // The SDK signs an https URL unless told otherwise, whatever the endpoint is. A plain-http endpoint (MinIO or
    // Garage in development) would then be handed a URL that cannot connect, so follow the endpoint's own scheme.
    private Amazon.S3.Protocol PresignProtocol =>
        client.Config.ServiceURL is { } serviceUrl && serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Amazon.S3.Protocol.HTTP
            : Amazon.S3.Protocol.HTTPS;

    /// <summary>Returns the permanent, absolute public URL for an object in the public container. The URL is
    /// composed at read time from <see cref="S3StorageOptions.PublicBaseUrl"/> and the key with its
    /// visibility prefix stripped and each path segment percent-encoded, so it survives a base-URL/CDN change
    /// and is never persisted.</summary>
    /// <param name="key">A public-container object key (one carrying the public visibility prefix).</param>
    /// <returns>The absolute public URL the object is served from.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="key"/> is not a
    /// public-container key, or when no public container is configured
    /// (<see cref="S3StorageOptions.PublicBucketName"/> / <see cref="S3StorageOptions.PublicBaseUrl"/> unset).</exception>
    public Uri GetPublicUrl(string key)
    {
        if (!Themia.Storage.StorageKey.IsPublic(key))
        {
            throw new InvalidOperationException(
                $"Object '{key}' is not in the public container; only a public object has a public URL. " +
                "Public visibility is chosen at write time and cannot be changed.");
        }

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            throw new InvalidOperationException("No public container is configured; set S3StorageOptions.PublicBucketName and PublicBaseUrl.");
        }

        return new Uri($"{publicBaseUrl.TrimEnd('/')}/{Themia.Storage.StorageKey.ToUrlPath(Themia.Storage.StorageKey.StripVisibilityPrefix(key))}");
    }

    // The key addresses its own container: a "public/" prefix selects the public bucket and is stripped,
    // so the object is stored under the same tail in a different bucket. Every S3 call routes through here.
    private (string Bucket, string Key) Resolve(string key)
    {
        if (!Themia.Storage.StorageKey.IsPublic(key))
        {
            if (publicOnly)
            {
                throw new InvalidOperationException(
                    $"Object '{key}' is not a public object and this S3 provider is public-only (S3StorageOptions.PublicOnly); " +
                    "it has no private bucket. Private keys belong to the other slot of the split.");
            }

            return (bucket, key);
        }

        if (string.IsNullOrWhiteSpace(publicBucket))
        {
            throw new InvalidOperationException("No public container is configured; set S3StorageOptions.PublicBucketName and PublicBaseUrl.");
        }

        return (publicBucket, Themia.Storage.StorageKey.StripVisibilityPrefix(key));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Only dispose the client this provider built; an externally-supplied client (test ctor) is
        // owned by the caller.
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    internal static IAmazonS3 BuildClient(S3StorageOptions options, HttpClientFactory? httpClientFactory = null)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (httpClientFactory is not null)
        {
            config.HttpClientFactory = httpClientFactory;
        }

        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl.AbsoluteUri;

            // The region only names the signing scope here, but a server such as Garage rejects a signature whose
            // region is not the one it is configured with ("SignatureDoesNotMatch").
            if (!string.IsNullOrWhiteSpace(options.Region))
            {
                config.AuthenticationRegion = options.Region;
            }

            // The SDK's default checksum is a CRC32 trailer. S3-compatible servers reject it on upload: Garage over
            // http ("Invalid payload signature", a signed trailer) and Cloudflare R2 over https (unsigned trailer
            // not accepted). So a checksum is added only where the service requires one, for every custom endpoint.
            // Real AWS (no ServiceUrl) keeps the SDK default.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) != string.IsNullOrWhiteSpace(options.SecretKey))
        {
            throw new ArgumentException("S3 credentials must be set together: provide both AccessKey and SecretKey, or neither (to use the default AWS credential chain).", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return new AmazonS3Client(config);
        }

        var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        // AWS SDK for .NET v4 resolves presigned-URL credentials from Config.DefaultAWSCredentials
        // (falling back to the default identity chain) — NOT from the credentials passed to the
        // client constructor. Set both so explicit keys (e.g. Cloudflare R2) sign presigned URLs.
        config.DefaultAWSCredentials = credentials;
        return new AmazonS3Client(credentials, config);
    }
}
