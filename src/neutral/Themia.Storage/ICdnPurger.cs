namespace Themia.Storage;

/// <summary>
/// Clears a CDN's cached copy of one public object. <see cref="PurgingStorageProvider"/> calls it after the
/// object has been deleted from storage; an implementation is vendor-specific
/// (<c>Themia.Storage.Cloudflare</c> is the first).
/// </summary>
public interface ICdnPurger
{
    /// <summary>Purges the cached copy of <paramref name="url"/>, the public URL the CDN was asked for.</summary>
    /// <param name="url">The absolute public URL of the deleted object.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <exception cref="CdnPurgeException">The CDN refused the purge, or the purge could not be confirmed.</exception>
    /// <exception cref="OperationCanceledException">
    /// The caller's own <paramref name="cancellationToken"/> was cancelled. It propagates as
    /// <see cref="OperationCanceledException"/> and is never reported as <see cref="CdnPurgeException"/>.
    /// </exception>
    Task PurgeAsync(Uri url, CancellationToken cancellationToken = default);
}
