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

    /// <summary>
    /// A server that accepts a connection and never answers. An <c>&lt;img&gt;</c> pointing at it keeps the
    /// page's load event from firing, so <c>SetContentAsync</c> never returns on its own — the shape of a
    /// template referencing an image or font on a host that stops responding.
    /// </summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly List<System.Net.Sockets.TcpClient> held = [];

        public SilentServer()
        {
            listener.Start();
            _ = AcceptForeverAsync();
        }

        public string Url => $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/never.png";

        private async Task AcceptForeverAsync()
        {
            try
            {
                while (true)
                {
                    held.Add(await listener.AcceptTcpClientAsync());   // hold it open; send nothing
                }
            }
            catch (ObjectDisposedException) { }
            catch (System.Net.Sockets.SocketException) { }
        }

        public void Dispose()
        {
            listener.Stop();
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public async Task A_render_that_never_finishes_loading_times_out_and_gives_its_slot_back()
    {
        // MaxConcurrency 1: before the deadline existed, this render held the only slot for ever and the
        // follow-up render below would have waited behind it indefinitely.
        using var server = new SilentServer();
        await using var renderer = new PuppeteerPdfRenderer(
            new ThemiaPdfOptions { MaxConcurrency = 1, RenderTimeout = TimeSpan.FromSeconds(3) },
            NullLogger<PuppeteerPdfRenderer>.Instance);
        await renderer.RenderHtmlAsync("<p>warm up: launch Chromium outside the timed render</p>");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => renderer.RenderHtmlAsync($"<img src=\"{server.Url}\">"));
        started.Stop();

        Assert.Contains(nameof(ThemiaPdfOptions.RenderTimeout), error.Message);
        // Well under PuppeteerSharp's own 30 s navigation timeout, so it was Themia's deadline that fired.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"timed out after {started.Elapsed}");

        // The slot came back: a normal render now completes — bounded, so a regression fails instead of hanging.
        var next = await renderer.RenderHtmlAsync("<p>after</p>").WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, next[..5]);
    }

    [Fact]
    public async Task Cancelling_a_stuck_render_gives_its_slot_back()
    {
        // The caller's token used to be honoured only BETWEEN page calls; a SetContent in flight ran to
        // completion — here, never.
        using var server = new SilentServer();
        await using var renderer = new PuppeteerPdfRenderer(
            new ThemiaPdfOptions { MaxConcurrency = 1 },
            NullLogger<PuppeteerPdfRenderer>.Instance);
        await renderer.RenderHtmlAsync("<p>warm up</p>");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderHtmlAsync($"<img src=\"{server.Url}\">", ct: cts.Token));

        var next = await renderer.RenderHtmlAsync("<p>after</p>").WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(next.Length > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RenderHtmlAsync_NeverExceedsMaxConcurrency(int maxConcurrency)
    {
        // Asserted on an observed peak rather than on elapsed time: a timing assertion cannot tell a working
        // gate from a slow machine, and would pass on a fast one even with the gate removed.
        await using var renderer = new PuppeteerPdfRenderer(
            new ThemiaPdfOptions { MaxConcurrency = maxConcurrency },
            NullLogger<PuppeteerPdfRenderer>.Instance);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => renderer.RenderHtmlAsync($"<p>{i}</p>")));

        Assert.All(results, b => Assert.True(b.Length > 0));
        Assert.True(
            renderer.PeakInFlight <= maxConcurrency,
            $"peak in-flight renders was {renderer.PeakInFlight}, expected at most {maxConcurrency}");

        // The gate must actually have been contended, otherwise the assertion above is satisfied trivially
        // by renders that happened to run one at a time.
        Assert.Equal(maxConcurrency, renderer.PeakInFlight);
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
