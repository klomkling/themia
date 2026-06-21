using Microsoft.Extensions.Logging;
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
        // The renderer logs "Chromium launched." once per browser launch; count those entries to
        // prove concurrent first-renders launch exactly one browser (the double-checked lock holds).
        var logger = new LaunchCountingLogger();
        await using var renderer = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), logger);

        var results = await Task.WhenAll(
            renderer.RenderHtmlAsync("<p>1</p>"),
            renderer.RenderHtmlAsync("<p>2</p>"),
            renderer.RenderHtmlAsync("<p>3</p>"));

        Assert.All(results, b => Assert.True(b.Length > 0));
        Assert.Equal(1, logger.LaunchCount);
    }

    // Counts the "Chromium launched." log entries the renderer emits per browser launch.
    private sealed class LaunchCountingLogger : ILogger<PuppeteerPdfRenderer>
    {
        private int _launchCount;

        public int LaunchCount => _launchCount;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("launched", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _launchCount);
            }
        }
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
