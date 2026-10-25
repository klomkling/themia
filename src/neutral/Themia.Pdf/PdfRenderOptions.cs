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

    /// <summary>Prints the page turned on its side (a wide page). Default <see langword="false"/>, portrait.</summary>
    public bool Landscape { get; init; }

    /// <summary>Top margin (CSS length, e.g. "20mm"). Default "20mm".</summary>
    public string MarginTop { get; init; } = "20mm";

    /// <summary>Bottom margin (CSS length). Default "20mm".</summary>
    public string MarginBottom { get; init; } = "20mm";

    /// <summary>Left margin (CSS length). Default "15mm".</summary>
    public string MarginLeft { get; init; } = "15mm";

    /// <summary>Right margin (CSS length). Default "15mm".</summary>
    public string MarginRight { get; init; } = "15mm";

    /// <summary>
    /// Whether Chromium writes a tagged (accessible) PDF. <see langword="null"/> keeps the PuppeteerSharp default.
    /// Set <see langword="false"/> when the bytes must be reproducible: a tagged PDF carries structure-element ids
    /// (<c>/ID (node00000034)</c>) taken from a counter that advances with every render in the same browser process.
    /// </summary>
    public bool? Tagged { get; init; }
}
