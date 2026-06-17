namespace Themia.Storage;

/// <summary>A storage backend over opaque string keys. Tenant-agnostic and framework-free: tenant
/// isolation, metadata, and quota are layered on by <c>Themia.Modules.Storage</c>. Keys are physical
/// keys (already prefixed by the caller); a provider does not interpret them beyond mapping to its
/// own namespace.</summary>
public interface IStorageProvider
{
    /// <summary>Writes <paramref name="content"/> at <paramref name="key"/>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="content">The content stream (read to its end).</param>
    /// <param name="options">Content type and write options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Metadata for the written object.</returns>
    Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads the object at <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The content stream + metadata, or <see langword="null"/>.</returns>
    Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Whether an object exists at <paramref name="key"/>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when present.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object at <paramref name="key"/>. Idempotent — deleting an absent key is a no-op.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned URL for a direct client transfer.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="request">The operation, expiry, and (for uploads) the required content type.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited URL.</returns>
    Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default);
}
