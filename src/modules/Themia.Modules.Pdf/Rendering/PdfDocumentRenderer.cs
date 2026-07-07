using Themia.Modules.Pdf.Store;
using Themia.Pdf;

namespace Themia.Modules.Pdf.Rendering;

// Scoped: depends on the tenant-scoped IPdfTemplateStore while the neutral renderers are singletons.
internal sealed class PdfDocumentRenderer(
    IPdfTemplateStore store,
    IHtmlTemplateRenderer htmlRenderer,
    IPdfRenderer pdfRenderer) : IPdfDocumentRenderer
{
    public async Task<byte[]> RenderAsync(
        string key, object model, PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        var template = await store.ResolveAsync(key, cancellationToken).ConfigureAwait(false);
        var html = htmlRenderer.Render(template.Body, model);
        return await pdfRenderer.RenderHtmlAsync(html, options, cancellationToken).ConfigureAwait(false);
    }
}
