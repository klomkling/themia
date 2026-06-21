namespace Themia.Pdf;

/// <summary>Paper size for a rendered PDF. Maps internally onto the PuppeteerSharp paper format.</summary>
public enum PdfPaperFormat
{
    /// <summary>A4 (210 × 297 mm). The default.</summary>
    A4 = 0,

    /// <summary>A3 (297 × 420 mm).</summary>
    A3,

    /// <summary>US Letter (8.5 × 11 in).</summary>
    Letter,

    /// <summary>US Legal (8.5 × 14 in).</summary>
    Legal,

    /// <summary>Tabloid (11 × 17 in).</summary>
    Tabloid,
}
