namespace Themia.Storage.Urls;

/// <summary>Mints presigned URLs a client can open, whichever provider is registered.</summary>
/// <remarks>
/// Lives in the neutral package rather than the ASP.NET one on purpose: a background job composing a
/// notification has no HTTP request to take a host from, and still needs an absolute link.
/// </remarks>
public interface IStorageUrlService
{
    /// <summary>An absolute presigned download URL for <paramref name="key"/>.</summary>
    /// <param name="key">The storage key.</param>
    /// <param name="lifetime">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The provider's URL untouched when it is already absolute (S3-compatible providers), otherwise the
    /// provider's relative URL joined onto <see cref="StorageUrlOptions.PresignedBaseUrl"/> (Local).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The provider returned a relative URL and <see cref="StorageUrlOptions.PresignedBaseUrl"/> is not set —
    /// thrown rather than returning a relative URL, which would travel into an email and break there.
    /// </exception>
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
