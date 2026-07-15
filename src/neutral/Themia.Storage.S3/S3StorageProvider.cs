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
    private readonly string publicBucket;
    private readonly string publicBaseUrl;
    private readonly bool ownsClient;

    /// <summary>Creates the provider, building the S3 client from <paramref name="options"/>.</summary>
    /// <param name="options">The S3 options.</param>
    public S3StorageProvider(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
        bucket = options.BucketName;
        options.Validate();
        publicBucket = options.PublicBucketName;
        publicBaseUrl = options.PublicBaseUrl;
        client = BuildClient(options);
        ownsClient = true;
    }

    /// <summary>Creates the provider over an existing client (used by tests against MinIO).</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="bucketName">The bucket name.</param>
    public S3StorageProvider(IAmazonS3 client, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        this.client = client;
        bucket = bucketName;
        publicBucket = string.Empty;
        publicBaseUrl = string.Empty;
        ownsClient = false;
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (resolvedBucket, resolvedKey) = Resolve(key);
        var contentType = string.IsNullOrEmpty(options.ContentType) ? "application/octet-stream" : options.ContentType;
        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = resolvedBucket,
            Key = resolvedKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken).ConfigureAwait(false);

        var length = content.CanSeek ? content.Length : 0;
        return new StorageObjectInfo(key, length, contentType, response.ETag);
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
        }).ConfigureAwait(false);
        return new Uri(url);
    }

    /// <inheritdoc />
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

        return new Uri($"{publicBaseUrl.TrimEnd('/')}/{Themia.Storage.StorageKey.StripVisibilityPrefix(key)}");
    }

    // The key addresses its own container: a "public/" prefix selects the public bucket and is stripped,
    // so the object is stored under the same tail in a different bucket. Every S3 call routes through here.
    private (string Bucket, string Key) Resolve(string key)
    {
        if (!Themia.Storage.StorageKey.IsPublic(key))
        {
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

    private static IAmazonS3 BuildClient(S3StorageOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl.AbsoluteUri;
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
