using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Themia.Pdf;

/// <summary>
/// PuppeteerSharp-backed <see cref="IPdfRenderer"/>. Lazily launches a single headless Chromium
/// browser (guarded by a semaphore), reuses it across renders, and disposes it on
/// <see cref="DisposeAsync"/> (or <see cref="Dispose"/> for non-async teardown).
/// </summary>
internal sealed class PuppeteerPdfRenderer : IPdfRenderer, IAsyncDisposable, IDisposable
{
    private readonly ThemiaPdfOptions _options;
    private readonly ILogger<PuppeteerPdfRenderer> _logger;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    // volatile: the outer fast-path read in EnsureBrowserAsync runs outside the lock, so the
    // canonical double-checked-locking form requires a fresh read of the published reference.
    private volatile IBrowser? _browser;
    private int _disposed;

    public PuppeteerPdfRenderer(ThemiaPdfOptions options, ILogger<PuppeteerPdfRenderer> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ct.ThrowIfCancellationRequested();
        var opts = options ?? new PdfRenderOptions();

        var browser = await EnsureBrowserAsync(ct).ConfigureAwait(false);

        // PuppeteerSharp's page methods don't take a CancellationToken, so honor ct between
        // stages — combined with the WaitAsync(ct) in EnsureBrowserAsync this is the available
        // granularity (a SetContent/PdfData call already in flight runs to completion). Render
        // failures propagate untouched to the top-level handler (THEMIA101: no log-and-rethrow).
        await using var page = await browser.NewPageAsync().ConfigureAwait(false);
        await page.SetContentAsync(html).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await page.PdfDataAsync(ToPdfOptions(opts)).ConfigureAwait(false);
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _browserLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            // A non-null _browser here is dead/disconnected — dispose it before relaunching so the
            // old websocket + Chromium process don't leak.
            _browser?.Dispose();
            _browser = await LaunchAsync().ConfigureAwait(false);
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task<IBrowser> LaunchAsync()
    {
        var launchOptions = new LaunchOptions
        {
            Headless = _options.Headless,
            Args = _options.LaunchArgs,
        };

        if (!string.IsNullOrEmpty(_options.ExecutablePath))
        {
            launchOptions.ExecutablePath = _options.ExecutablePath;
        }
        else if (!_options.DisableAutoDownload)
        {
            _logger.LogInformation("Themia.Pdf: downloading/launching Chromium…");
            await new BrowserFetcher().DownloadAsync().ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                "Themia.Pdf: no Chromium available. Set ThemiaPdfOptions.ExecutablePath, or leave " +
                "DisableAutoDownload=false to allow the runtime download.");
        }

        var browser = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
        _logger.LogInformation("Themia.Pdf: Chromium launched.");
        return browser;
    }

    // internal (not private) so the pure format/margin mapping can be unit-tested without Chromium.
    internal static PdfOptions ToPdfOptions(PdfRenderOptions o) => new()
    {
        Format = o.PaperFormat switch
        {
            PdfPaperFormat.A3 => PaperFormat.A3,
            PdfPaperFormat.Letter => PaperFormat.Letter,
            PdfPaperFormat.Legal => PaperFormat.Legal,
            PdfPaperFormat.Tabloid => PaperFormat.Tabloid,
            _ => PaperFormat.A4,
        },
        PrintBackground = o.PrintBackground,
        MarginOptions = new MarginOptions
        {
            Top = o.MarginTop,
            Bottom = o.MarginBottom,
            Left = o.MarginLeft,
            Right = o.MarginRight,
        },
    };

    public void Dispose()
    {
        // Single-shot guard: first caller wins; a second Dispose/DisposeAsync is a no-op.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Synchronous path (e.g. a non-async DI container teardown). PuppeteerSharp's
        // Browser.Dispose() blocks on its async close, so the process is still shut down;
        // an async host disposes via DisposeAsync below, which closes cooperatively.
        _browser?.Dispose();
        // _browserLock is intentionally NOT disposed: SemaphoreSlim only needs disposal when its
        // AvailableWaitHandle was accessed (we never do), and disposing it while an in-flight
        // render is between WaitAsync and Release would make that Release throw ObjectDisposedException.
    }

    public async ValueTask DisposeAsync()
    {
        // Single-shot guard: first caller wins; a second Dispose/DisposeAsync is a no-op.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser.Dispose();
        }

        // _browserLock intentionally not disposed — see Dispose() for rationale.
    }
}
