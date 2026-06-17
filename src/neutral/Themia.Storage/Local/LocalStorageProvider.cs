namespace Themia.Storage.Local;

/// <summary>A filesystem-backed <see cref="IStorageProvider"/>. Maps a sanitized key to a path under
/// <see cref="LocalStorageOptions.RootPath"/>. Blobs live under <c>{root}/blobs</c> and content-type
/// sidecars under <c>{root}/content-types</c> in separate subtrees, so a user key can never collide with
/// another key's sidecar. Intended for development and single-node deployments; production multi-node
/// setups use the S3/R2 backend.</summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private const string BlobsDir = "blobs";
    private const string ContentTypesDir = "content-types";

    private readonly LocalStorageOptions options;
    private readonly LocalUrlSigner? signer;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The filesystem options.</param>
    public LocalStorageProvider(LocalStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        this.options = options;
        this.signer = string.IsNullOrWhiteSpace(options.SigningKey) ? null : new LocalUrlSigner(options.SigningKey);
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var blobPath = ResolveBlobPath(key);
        if (!options.Overwrite && File.Exists(blobPath))
        {
            throw new IOException($"An object already exists at '{key}' and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        var contentType = string.IsNullOrEmpty(options.ContentType) ? "application/octet-stream" : options.ContentType;

        // Atomic overwrite: write the blob to a temp file on the same volume, then move it into place.
        // A failure/cancel mid-copy must never truncate or corrupt the existing target, so File.Create
        // (which truncates first) is not used directly on the final path.
        var length = await WriteAtomicAsync(blobPath, content, cancellationToken).ConfigureAwait(false);

        // Sidecar is written only after the blob is safely in place, so a failed blob write never leaves
        // a divergent content type behind.
        var typePath = ResolveContentTypePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(typePath)!);
        await WriteTextAtomicAsync(typePath, contentType, cancellationToken).ConfigureAwait(false);
        return new StorageObjectInfo(key, length, contentType, ETag: null);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var blobPath = ResolveBlobPath(key);
        if (!File.Exists(blobPath))
        {
            return null;
        }

        var typePath = ResolveContentTypePath(key);
        var contentType = File.Exists(typePath)
            ? await File.ReadAllTextAsync(typePath, cancellationToken).ConfigureAwait(false)
            : "application/octet-stream";

        FileStream stream;
        try
        {
            stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The file was deleted concurrently after the File.Exists check (TOCTOU); honor the
            // absent-object contract (null) rather than surfacing the race as an exception.
            return null;
        }

        return new StorageReadResult(stream, contentType, stream.Length);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var exists = File.Exists(ResolveBlobPath(key));
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        var blobPath = ResolveBlobPath(key);
        if (!File.Exists(blobPath))
        {
            return null;
        }

        var typePath = ResolveContentTypePath(key);
        var contentType = File.Exists(typePath)
            ? await File.ReadAllTextAsync(typePath, cancellationToken).ConfigureAwait(false)
            : "application/octet-stream";

        return new StorageObjectInfo(key, new FileInfo(blobPath).Length, contentType, ETag: null);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var blobPath = ResolveBlobPath(key);
        if (File.Exists(blobPath)) File.Delete(blobPath);
        var typePath = ResolveContentTypePath(key);
        if (File.Exists(typePath)) File.Delete(typePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        if (signer is null)
        {
            throw new ArgumentException("SigningKey must be set to issue presigned URLs.", nameof(request));
        }

        ResolveBlobPath(key); // validates the key
        var op = request.Operation == PresignedUrlOperation.Put ? "put" : "get";
        var token = signer.Sign(key, request.Operation, DateTimeOffset.UtcNow.Add(request.Expiry));
        // Relative URI the module's MapThemiaStorageEndpoints _local route materializes + verifies.
        // The token signs the PHYSICAL key + op; the endpoint calls the provider directly with this key.
        var uri = new Uri($"_local/{op}?key={Uri.EscapeDataString(key)}&token={Uri.EscapeDataString(token)}", UriKind.Relative);
        return Task.FromResult(uri);
    }

    // Writes the blob to a temp file on the same volume, then atomically moves it onto the final path.
    // On any failure the temp file is deleted (best-effort) and the original target is left untouched.
    private static async Task<long> WriteAtomicAsync(string finalPath, Stream content, CancellationToken cancellationToken)
    {
        var tmpPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            long length;
            await using (var file = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                length = file.Length;
            }

            File.Move(tmpPath, finalPath, overwrite: true);
            return length;
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    private static async Task WriteTextAtomicAsync(string finalPath, string text, CancellationToken cancellationToken)
    {
        var tmpPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tmpPath, text, cancellationToken).ConfigureAwait(false);
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of the temp file; nothing actionable if it fails.
        }
    }

    // Maps a key to an absolute blob path under {root}/blobs.
    private string ResolveBlobPath(string key) => ResolveUnder(BlobsDir, key);

    // Maps a key to an absolute content-type sidecar path under {root}/content-types.
    private string ResolveContentTypePath(string key) => ResolveUnder(ContentTypesDir, key);

    // Maps a key to an absolute path UNDER {root}/{subdir}, rejecting traversal/absolute keys by verifying
    // the resolved full path stays within that subtree.
    private string ResolveUnder(string subdir, string key)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);

        var subRoot = Path.GetFullPath(Path.Combine(options.RootPath, subdir));
        var full = Path.GetFullPath(Path.Combine(subRoot, normalized));
        if (!full.StartsWith(subRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !full.Equals(subRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid object key '{key}': resolves outside the storage root.", nameof(key));
        }

        return full;
    }
}
