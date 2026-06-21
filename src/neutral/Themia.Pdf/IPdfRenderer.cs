namespace Themia.Pdf;

/// <summary>Prints HTML to a PDF using headless Chromium.</summary>
public interface IPdfRenderer
{
    /// <summary>Renders <paramref name="html"/> to PDF bytes.</summary>
    /// <param name="html">The HTML document to print.</param>
    /// <param name="options">Output options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The PDF as a byte array.</returns>
    Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default);
}
