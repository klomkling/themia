namespace Themia.ETax;

/// <summary>
/// Where the paged templates (versions "4" and "5") get a seller's logo. A document records the SHA-256 of the logo it was issued with, so a
/// later copy or the ORIGINAL send must find exactly that image: keep the logos of earlier brandings for as long as their documents may be rendered.
/// Both calls run on the issue path (inside the caller's transaction) and on every render, so serve them from memory or a cache.
/// </summary>
public interface ITaxDocumentLogoSource
{
    /// <summary>The logo a NEW document of <paramref name="sellerId"/> is issued with, or <see langword="null"/> for none: no logo box is printed and no hash is recorded.</summary>
    /// <param name="sellerId">The seller.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaxDocumentLogo?> GetCurrentAsync(string sellerId, CancellationToken ct = default);

    /// <summary>The logo of <paramref name="sellerId"/> whose <see cref="TaxDocumentLogo.Sha256"/> is <paramref name="sha256"/>, or <see langword="null"/> when it is no longer held.</summary>
    /// <param name="sellerId">The seller.</param>
    /// <param name="sha256">The hash recorded on the document.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaxDocumentLogo?> FindAsync(string sellerId, string sha256, CancellationToken ct = default);
}
