using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Tenant-aware object storage. Operates on logical keys (callers never see the physical
/// tenant-prefixed key) and enforces validation, scanning, metadata, and per-tenant quota.</summary>
public interface ITenantStorage
{
    /// <summary>Validates, scans, quota-checks, stores, and records an object.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="content">The content stream.</param>
    /// <param name="options">Content type and write options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stored-object metadata.</returns>
    Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads an object by logical key, or <see langword="null"/> when absent in the tenant.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The content + metadata, or <see langword="null"/>.</returns>
    Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Whether an object with the logical key exists in the tenant.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when present.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object (soft-deletes its metadata, then removes the blob).</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned download URL for the object.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="expiry">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited download URL.</returns>
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned upload URL for the object (the client uploads directly to the backend).</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="contentType">The content type the upload must declare.</param>
    /// <param name="expiry">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited upload URL.</returns>
    Task<Uri> GetUploadUrlAsync(string key, string contentType, TimeSpan expiry, CancellationToken cancellationToken = default);
}
