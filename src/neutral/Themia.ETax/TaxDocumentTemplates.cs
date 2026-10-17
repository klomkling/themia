namespace Themia.ETax;

/// <summary>
/// The pinned template versions. A document records the version it was issued under and is always rendered
/// with it, so a template change ships as a NEW version and never edits an old one.
/// </summary>
public static class TaxDocumentTemplates
{
    /// <summary>The version new documents are issued under unless the adopter opts into <see cref="LatestVersion"/> (<see cref="TaxDocumentTemplateOptions.TemplateVersion"/>).</summary>
    public const string CurrentVersion = "1";

    /// <summary>
    /// The newest version, which prints the amount in Thai words under the total in <see cref="ThaiBahtTextEtMode.TensOnly"/> (หนึ่งร้อยหนึ่ง but ยี่สิบเอ็ด).
    /// Version <c>"2"</c> prints the same words with "เอ็ด" (<see cref="ThaiBahtTextEtMode.Always"/>). The convention is part of the version, so a document
    /// issued under either re-renders to the same bytes whatever the adopter configures later. Adopters opt into either for new documents.
    /// </summary>
    public const string LatestVersion = "3";

    private static readonly Lazy<string> Version1 = new(() =>
        System.Text.Encoding.UTF8.GetString(TaxDocumentFonts.ReadBytes("Templates.tax-document.v1.hbs")));

    private static readonly Lazy<string> Version2 = new(() =>
        System.Text.Encoding.UTF8.GetString(TaxDocumentFonts.ReadBytes("Templates.tax-document.v2.hbs")));

    /// <summary>True when <paramref name="templateVersion"/> can still be rendered.</summary>
    /// <param name="templateVersion">The version recorded on a document.</param>
    public static bool IsSupported(string? templateVersion) => templateVersion is "1" or "2" or "3";

    // Versions 2 and 3 share one layout; the version alone fixes how the amount in words reads a units digit of 1.
    internal static ThaiBahtTextEtMode EtModeOf(string templateVersion) => templateVersion switch
    {
        "3" => ThaiBahtTextEtMode.TensOnly,
        _ => ThaiBahtTextEtMode.Always,
    };

    internal static string Get(string templateVersion) => templateVersion switch
    {
        "1" => Version1.Value,
        "2" or "3" => Version2.Value,
        _ => throw new ArgumentException($"Unknown tax document template version '{templateVersion}'.", nameof(templateVersion)),
    };
}
