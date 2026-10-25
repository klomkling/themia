namespace Themia.ETax;

/// <summary>
/// The logo a document was issued with cannot be printed: the source no longer holds it, or the logo passed in is not the one recorded.
/// Permanent until the host brings the image back, so the sender fails the document (<c>logo_unavailable</c>) instead of retrying.
/// </summary>
public sealed class TaxDocumentLogoException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="logoSha256">The hash recorded on the document.</param>
    public TaxDocumentLogoException(string logoSha256)
        : base($"The logo recorded on the document (SHA-256 {Short(logoSha256)}...) is not available.") => LogoSha256 = logoSha256;

    /// <summary>The hash recorded on the document.</summary>
    public string LogoSha256 { get; }

    private static string Short(string sha256) => sha256.Length <= 8 ? sha256 : sha256[..8];
}
