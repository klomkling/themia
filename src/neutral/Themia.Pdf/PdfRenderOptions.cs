namespace Themia.Pdf;

/// <summary>
/// Per-render output options for IPdfRenderer. Defaults match the ported ezy-assets
/// contract renderer: A4, printed backgrounds, 20 mm top/bottom and 15 mm left/right margins.
/// </summary>
public sealed class PdfRenderOptions
{
    /// <summary>Paper size. Default <see cref="PdfPaperFormat.A4"/>.</summary>
    public PdfPaperFormat PaperFormat { get; set; } = PdfPaperFormat.A4;

    /// <summary>Whether CSS backgrounds are printed. Default <see langword="true"/>.</summary>
    public bool PrintBackground { get; set; } = true;

    /// <summary>Top margin (CSS length, e.g. "20mm"). Default "20mm".</summary>
    public string MarginTop { get; set; } = "20mm";

    /// <summary>Bottom margin (CSS length). Default "20mm".</summary>
    public string MarginBottom { get; set; } = "20mm";

    /// <summary>Left margin (CSS length). Default "15mm".</summary>
    public string MarginLeft { get; set; } = "15mm";

    /// <summary>Right margin (CSS length). Default "15mm".</summary>
    public string MarginRight { get; set; } = "15mm";
}
