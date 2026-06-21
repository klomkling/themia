namespace Themia.Pdf;

/// <summary>
/// Per-render output options for <see cref="IPdfRenderer"/>. Defaults match the ported ezy-assets
/// contract renderer: A4, printed backgrounds, 20 mm top/bottom and 15 mm left/right margins.
/// </summary>
public sealed class PdfRenderOptions
{
    // init-only: a per-render value object handed to the singleton renderer; immutability after
    // construction removes any mid-render mutation race between concurrent RenderHtmlAsync calls.

    /// <summary>Paper size. Default <see cref="PdfPaperFormat.A4"/>.</summary>
    public PdfPaperFormat PaperFormat { get; init; } = PdfPaperFormat.A4;

    /// <summary>Whether CSS backgrounds are printed. Default <see langword="true"/>.</summary>
    public bool PrintBackground { get; init; } = true;

    /// <summary>Top margin (CSS length, e.g. "20mm"). Default "20mm".</summary>
    public string MarginTop { get; init; } = "20mm";

    /// <summary>Bottom margin (CSS length). Default "20mm".</summary>
    public string MarginBottom { get; init; } = "20mm";

    /// <summary>Left margin (CSS length). Default "15mm".</summary>
    public string MarginLeft { get; init; } = "15mm";

    /// <summary>Right margin (CSS length). Default "15mm".</summary>
    public string MarginRight { get; init; } = "15mm";
}
