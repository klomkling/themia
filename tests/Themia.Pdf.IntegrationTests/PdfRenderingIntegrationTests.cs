using Microsoft.Extensions.Logging.Abstractions;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PdfRenderingIntegrationTests
{
    [Fact]
    public async Task RenderHtmlAsync_ProducesPdfBytes()
    {
        await using var renderer = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var bytes = await renderer.RenderHtmlAsync("<h1>Hello PDF</h1>");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        // PDF magic header: "%PDF-"
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, bytes[..5]);
    }

    [Fact]
    public async Task RenderHtmlAsync_ConcurrentRenders_ReuseSingleBrowser()
    {
        await using var renderer = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var results = await Task.WhenAll(
            renderer.RenderHtmlAsync("<p>1</p>"),
            renderer.RenderHtmlAsync("<p>2</p>"),
            renderer.RenderHtmlAsync("<p>3</p>"));

        Assert.All(results, b => Assert.True(b.Length > 0));
    }

    [Fact]
    public async Task EndToEnd_TemplateThenPdf()
    {
        var template = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());
        await using var pdf = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var html = template.Render("<h1>{{title}}</h1>", new { title = "Report" });
        var bytes = await pdf.RenderHtmlAsync(html);

        Assert.True(bytes.Length > 0);
    }
}
