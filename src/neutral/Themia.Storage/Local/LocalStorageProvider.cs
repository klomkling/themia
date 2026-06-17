namespace Themia.Storage.Local;

/// <summary>A filesystem-backed <see cref="IStorageProvider"/>. Maps a sanitized key to a path under
/// <see cref="LocalStorageOptions.RootPath"/>. Intended for development and single-node deployments;
/// production multi-node setups use the S3/R2 backend.</summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private const string ContentTypeSuffix = ".contenttype";

    private readonly LocalStorageOptions options;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The filesystem options.</param>
    public LocalStorageProvider(LocalStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key);
        if (!options.Overwrite && File.Exists(path))
        {
            throw new IOException($"An object already exists at '{key}' and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long length;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            length = file.Length;
        }

        await File.WriteAllTextAsync(path + ContentTypeSuffix, options.ContentType, cancellationToken).ConfigureAwait(false);
        return new StorageObjectInfo(key, length, options.ContentType, ETag: null);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = File.Exists(path + ContentTypeSuffix)
            ? await File.ReadAllTextAsync(path + ContentTypeSuffix, cancellationToken).ConfigureAwait(false)
            : "application/octet-stream";
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new StorageReadResult(stream, contentType, stream.Length);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var exists = File.Exists(ResolvePath(key));
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ContentTypeSuffix)) File.Delete(path + ContentTypeSuffix);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    // Maps a key to an absolute path UNDER RootPath, rejecting traversal/absolute keys by verifying the
    // resolved full path stays within the root.
    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{key}': absolute paths and '..' segments are not allowed.", nameof(key));
        }

        var rootFull = Path.GetFullPath(options.RootPath);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !full.Equals(rootFull, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid object key '{key}': resolves outside the storage root.", nameof(key));
        }

        return full;
    }
}
