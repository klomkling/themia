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

    /// <summary>Validates and quota-reserves an object, then issues a presigned upload URL (the client
    /// uploads directly to the backend). A quota-counted but <em>pending</em> metadata row is reserved
    /// up front at the declared <paramref name="sizeBytes"/>; the reservation is invisible to reads until
    /// the client uploads the bytes and calls <see cref="CompleteUploadAsync"/>, which reconciles the
    /// quota to the actual stored size and makes the object visible.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="contentType">The content type the upload must declare.</param>
    /// <param name="sizeBytes">The declared object size in bytes (reserved against quota up front).</param>
    /// <param name="expiry">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited upload URL.</returns>
    Task<Uri> GetUploadUrlAsync(string key, string contentType, long sizeBytes, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>Confirms a presigned upload: stats the actually-stored bytes, validates and re-checks the
    /// per-tenant quota against the <em>actual</em> size, then commits the pending reservation (recording
    /// the actual size + etag and marking the object visible). On a quota overrun the orphaned blob and
    /// reservation are discarded and a <see cref="StorageQuotaExceededException"/> is thrown.</summary>
    /// <param name="key">The logical key whose presigned upload to confirm.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The committed stored-object metadata.</returns>
    Task<StoredObject> CompleteUploadAsync(string key, CancellationToken cancellationToken = default);
}
