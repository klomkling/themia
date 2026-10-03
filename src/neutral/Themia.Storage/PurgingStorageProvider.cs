namespace Themia.Storage;

/// <summary>
/// An <see cref="IStorageProvider"/> that, after deleting a <c>public/</c> object, purges the CDN's copy of
/// the URL <c>inner.GetPublicUrl(key)</c> (coord #0153). Wrap the public slot of a split provider, or the
/// single provider. Every member normalises the key first, as the split router does, and hands the inner
/// provider that key, so a put, a read and a delete address the same object even over a provider that does
/// not normalise (S3). A non-public key is never purged.
/// This type never disposes <c>inner</c>: whoever constructs it owns it.
/// </summary>
public sealed class PurgingStorageProvider : IStorageProvider
{
    private readonly IStorageProvider inner;
    private readonly ICdnPurger purger;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The provider that serves the public URLs.</param>
    /// <param name="purger">The CDN purger called after a public delete.</param>
    public PurgingStorageProvider(IStorageProvider inner, ICdnPurger purger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(purger);
        this.inner = inner;
        this.purger = purger;
    }

    /// <inheritdoc />
    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) =>
        inner.PutAsync(StorageKey.NormalizeAndValidate(key), content, options, cancellationToken);

    /// <inheritdoc />
    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetAsync(StorageKey.NormalizeAndValidate(key), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(StorageKey.NormalizeAndValidate(key), cancellationToken);

    /// <inheritdoc />
    public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) =>
        inner.StatAsync(StorageKey.NormalizeAndValidate(key), cancellationToken);

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        inner.GetPresignedUrlAsync(StorageKey.NormalizeAndValidate(key), request, cancellationToken);

    /// <inheritdoc />
    public Uri GetPublicUrl(string key) => inner.GetPublicUrl(StorageKey.NormalizeAndValidate(key));

    /// <summary>
    /// Deletes the object, then, for a <c>public/</c> key, purges its public URL. The URL is built first so a
    /// public slot with no public container fails before anything is deleted. If the token is cancelled between
    /// the delete and the purge, an <see cref="OperationCanceledException"/> is thrown with the object already
    /// deleted, so the caller should repeat the delete and purge, same as for <see cref="CdnPurgeException"/>.
    /// </summary>
    /// <exception cref="CdnPurgeException">
    /// The object is deleted but the purge step failed for any reason (the CDN refused it, or the purger or its
    /// HTTP pipeline threw something else); the CDN may still serve the object.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the object may already be deleted.</exception>
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        var publicUrl = StorageKey.IsPublic(normalized) ? inner.GetPublicUrl(normalized) : null;

        await inner.DeleteAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (publicUrl is not null)
        {
            await PurgeAfterDeleteAsync(publicUrl, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PurgeAfterDeleteAsync(Uri publicUrl, CancellationToken cancellationToken)
    {
        try
        {
            await purger.PurgeAsync(publicUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CdnPurgeException
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new CdnPurgeException(
                publicUrl,
                $"The purge failed after the object was deleted from storage ({ex.GetType().Name}); " +
                $"the CDN may still serve {publicUrl.AbsoluteUri}. Repeat the delete and purge.",
                httpStatus: null,
                innerException: ex);
        }
    }
}
