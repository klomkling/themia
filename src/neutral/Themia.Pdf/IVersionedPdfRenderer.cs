namespace Themia.Pdf;

/// <summary>
/// An <see cref="IPdfRenderer"/> that also reports which browser build printed the PDF. A separate interface,
/// so adopters' own <see cref="IPdfRenderer"/> test doubles keep compiling.
/// </summary>
public interface IVersionedPdfRenderer : IPdfRenderer
{
    /// <summary>Renders <paramref name="html"/> to PDF bytes and reports the browser that produced them.</summary>
    /// <param name="html">The HTML document to print.</param>
    /// <param name="options">Output options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancellation token, observed as in <see cref="IPdfRenderer.RenderHtmlAsync"/>.</param>
    /// <returns>The PDF and the version of the browser instance that printed it.</returns>
    Task<PdfRenderResult> RenderHtmlWithVersionAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default);
}
