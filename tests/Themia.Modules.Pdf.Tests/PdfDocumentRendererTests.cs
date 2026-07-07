using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;
using Themia.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfDocumentRendererTests
{
    [Fact]
    public async Task Resolves_then_merges_then_prints()
    {
        var store = new FakeStore(new PdfTemplate { Key = "invoice", Body = "TEMPLATE_BODY" });
        var html = new FakeHtml();
        var pdf = new FakePdf();
        var sut = new PdfDocumentRenderer(store, html, pdf);

        var bytes = await sut.RenderAsync("invoice", new { Total = 5 });

        Assert.Equal("TEMPLATE_BODY", html.LastTemplate);   // resolved body merged
        Assert.Equal(html.Returned, pdf.LastHtml);          // merged html printed
        Assert.Equal(pdf.Bytes, bytes);
    }

    private sealed class FakeStore(PdfTemplate t) : IPdfTemplateStore
    {
        public Task<PdfTemplate> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult(t);
        public Task<PdfTemplate> CreateAsync(PdfTemplate x, CancellationToken ct = default) => Task.FromResult(x);
        public Task<PdfTemplate> UpdateAsync(PdfTemplate x, CancellationToken ct = default) => Task.FromResult(x);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PdfTemplate?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<PdfTemplate?>(t);
        public Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PdfTemplate>>([t]);
    }

    private sealed class FakeHtml : IHtmlTemplateRenderer
    {
        public string? LastTemplate { get; private set; }
        public string Returned => "MERGED_HTML";
        public string Render(string template, object model) { LastTemplate = template; return Returned; }
    }

    private sealed class FakePdf : IPdfRenderer
    {
        public string? LastHtml { get; private set; }
        public byte[] Bytes { get; } = [1, 2, 3];
        public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
        { LastHtml = html; return Task.FromResult(Bytes); }
    }
}
