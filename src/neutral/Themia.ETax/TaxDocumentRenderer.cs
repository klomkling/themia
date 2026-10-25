using System.Security.Cryptography;
using HandlebarsDotNet;
using Themia.Pdf;

namespace Themia.ETax;

/// <summary>
/// Renders a stored <see cref="TaxDocumentSnapshot"/> to a normalised PDF. Reads only the snapshot and the
/// pinned template version, never today's VAT or withholding policy, so a later copy matches the original except
/// for its marker. Does not validate (stored documents are never re-validated) and does not log.
/// </summary>
public sealed class TaxDocumentRenderer
{
    private static readonly Lazy<string> FontCss = new(TaxDocumentRenderModel.FontFaceCss);

    internal static readonly PdfRenderOptions PrintOptions = new()
    {
        PaperFormat = PdfPaperFormat.A4,
        PrintBackground = true,
        // A tagged PDF embeds structure-element ids from a per-browser counter, so two renders would never match.
        Tagged = false,
        MarginTop = "15mm",
        MarginBottom = "15mm",
        MarginLeft = "15mm",
        MarginRight = "15mm",
    };

    // Version "4" is an A5 landscape page that carries its own 7 mm / 9 mm margins inside the template, so the PDF itself has none.
    internal static readonly PdfRenderOptions A5LandscapePrintOptions = new()
    {
        PaperFormat = PdfPaperFormat.A5,
        Landscape = true,
        PrintBackground = true,
        Tagged = false,
        MarginTop = "0mm",
        MarginBottom = "0mm",
        MarginLeft = "0mm",
        MarginRight = "0mm",
    };

    // Deliberately NOT the shared IHtmlTemplateRenderer: that one runs the adopter's ThemiaPdfOptions.ConfigureHandlebars
    // (extra helpers, overridden built-ins, changed escaping) for the adopter's own templates, and any of those would
    // silently change what a pinned template version prints. This instance has no adopter hooks.
    private static readonly IHandlebars Handlebars = HandlebarsDotNet.Handlebars.Create();

    private readonly IVersionedPdfRenderer _pdf;

    /// <summary>Creates the renderer.</summary>
    /// <param name="pdf">Prints the HTML and reports the browser version.</param>
    public TaxDocumentRenderer(IVersionedPdfRenderer pdf)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
    }

    /// <summary>Renders <paramref name="snapshot"/> with the template it was issued under.</summary>
    /// <param name="snapshot">The hash-verified snapshot read back from storage.</param>
    /// <param name="templateVersion">The template version recorded on the document.</param>
    /// <param name="marker">ORIGINAL on the send path only; COPY otherwise; CancelledCopy for a cancelled document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="templateVersion"/> is not a known version.</exception>
    public async Task<TaxDocumentRender> RenderAsync(
        TaxDocumentSnapshot snapshot, string templateVersion, TaxDocumentMarker marker, CancellationToken ct = default) =>
        await RenderWithLogoAsync(snapshot, templateVersion, marker, logo: null, ct).ConfigureAwait(false);

    /// <summary>Renders a snapshot like <see cref="RenderAsync"/>, printing <paramref name="logo"/> in the logo slot of the A5 template (version <c>"4"</c>).</summary>
    /// <param name="snapshot">The document content.</param>
    /// <param name="templateVersion">The version recorded on the document.</param>
    /// <param name="marker">Original, copy or cancelled copy.</param>
    /// <param name="logo">The seller's logo, or <see langword="null"/> for none: no logo box is printed. Templates 1 to 3 have no logo slot and ignore it.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TaxDocumentRender> RenderWithLogoAsync(
        TaxDocumentSnapshot snapshot, string templateVersion, TaxDocumentMarker marker, TaxDocumentLogo? logo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(templateVersion);

        var html = RenderHtml(snapshot, templateVersion, marker, logo);
        var options = TaxDocumentTemplates.IsA5Landscape(templateVersion) ? A5LandscapePrintOptions : PrintOptions;
        var printed = await _pdf.RenderHtmlWithVersionAsync(html, options, ct).ConfigureAwait(false);
        var pdf = TaxDocumentPdfNormaliser.NormaliseDates(printed.Bytes, snapshot.IssueDate);
        return new TaxDocumentRender(
            pdf,
            Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(),
            $"{printed.RendererVersion}|{TaxDocumentPdfNormaliser.Version}");
    }

    internal string RenderHtml(TaxDocumentSnapshot snapshot, string templateVersion, TaxDocumentMarker marker, TaxDocumentLogo? logo = null)
    {
        var model = TaxDocumentTemplates.IsA5Landscape(templateVersion)
            ? TaxDocumentA5Model.Build(snapshot, marker, FontCss.Value, TaxDocumentTemplates.EtModeOf(templateVersion), logo)
            : TaxDocumentRenderModel.Build(snapshot, marker, FontCss.Value, TaxDocumentTemplates.EtModeOf(templateVersion));
        return Handlebars.Compile(TaxDocumentTemplates.Get(templateVersion))(model);
    }
}
