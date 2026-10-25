namespace Themia.ETax;

// The default source: the one logo in the options, for every seller. It serves only its own hash, so after the host swaps the logo, documents
// issued with the old one are reported as unavailable instead of being printed with the new one.
internal sealed class OptionsTaxDocumentLogoSource(TaxDocumentTemplateOptions options) : ITaxDocumentLogoSource
{
    public Task<TaxDocumentLogo?> GetCurrentAsync(string sellerId, CancellationToken ct = default) => Task.FromResult(options.Logo);

    public Task<TaxDocumentLogo?> FindAsync(string sellerId, string sha256, CancellationToken ct = default) =>
        Task.FromResult(options.Logo is { } logo && string.Equals(logo.Sha256, sha256, StringComparison.Ordinal) ? logo : null);
}
