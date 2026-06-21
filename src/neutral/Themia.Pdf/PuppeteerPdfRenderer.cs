using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Themia.Pdf;

/// <summary>
/// PuppeteerSharp-backed <see cref="IPdfRenderer"/>. Lazily launches a single headless Chromium
/// browser (guarded by a semaphore), reuses it across renders, and disposes it on
/// <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class PuppeteerPdfRenderer : IPdfRenderer, IAsyncDisposable, IDisposable
{
    private readonly ThemiaPdfOptions _options;
    private readonly ILogger<PuppeteerPdfRenderer> _logger;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IBrowser? _browser;

    public PuppeteerPdfRenderer(ThemiaPdfOptions options, ILogger<PuppeteerPdfRenderer> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        var opts = options ?? new PdfRenderOptions();

        var browser = await EnsureBrowserAsync(ct).ConfigureAwait(false);

        await using var page = await browser.NewPageAsync().ConfigureAwait(false);
        await page.SetContentAsync(html).ConfigureAwait(false);
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

    private static PdfOptions ToPdfOptions(PdfRenderOptions o) => new()
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
        // Synchronous path: browser is null until first render, so this is safe in DI teardown
        // before any render has occurred. If a browser was launched, callers should prefer
        // DisposeAsync to avoid blocking.
        _browser?.Dispose();
        _browserLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser.Dispose();
        }

        _browserLock.Dispose();
    }
}
