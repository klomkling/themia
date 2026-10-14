namespace Themia.ETax;

/// <summary>
/// The pinned template versions. A document records the version it was issued under and is always rendered
/// with it, so a template change ships as a NEW version and never edits an old one.
/// </summary>
public static class TaxDocumentTemplates
{
    /// <summary>The version new documents are issued under.</summary>
    public const string CurrentVersion = "1";

    private static readonly Lazy<string> Version1 = new(() =>
        System.Text.Encoding.UTF8.GetString(TaxDocumentFonts.ReadBytes("Templates.tax-document.v1.hbs")));

    /// <summary>True when <paramref name="templateVersion"/> can still be rendered.</summary>
    /// <param name="templateVersion">The version recorded on a document.</param>
    public static bool IsSupported(string? templateVersion) => templateVersion == "1";

    internal static string Get(string templateVersion) => templateVersion switch
    {
        "1" => Version1.Value,
        _ => throw new ArgumentException($"Unknown tax document template version '{templateVersion}'.", nameof(templateVersion)),
    };
}
