using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp.Media;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class PuppeteerPdfRendererTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_MaxConcurrencyBelowOne_Throws(int maxConcurrency)
    {
        // A semaphore's capacity is fixed at construction, so an invalid bound has to fail here rather than
        // at the first render — and zero would deadlock every caller forever rather than erroring.
        var options = new ThemiaPdfOptions { MaxConcurrency = maxConcurrency };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance));

        Assert.Contains(nameof(ThemiaPdfOptions.MaxConcurrency), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Ctor_NonPositiveRenderTimeout_Throws(int milliseconds)
    {
        var options = new ThemiaPdfOptions { RenderTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance));

        Assert.Contains(nameof(ThemiaPdfOptions.RenderTimeout), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Ctor_NonPositiveLaunchTimeout_Throws(int milliseconds)
    {
        var options = new ThemiaPdfOptions { LaunchTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance));

        Assert.Contains(nameof(ThemiaPdfOptions.LaunchTimeout), ex.Message);
    }

    [Fact]
    public void Ctor_LaunchTimeoutExceedingIntMaxMilliseconds_Throws()
    {
        var options = new ThemiaPdfOptions { LaunchTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1000) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance));

        Assert.Contains(nameof(ThemiaPdfOptions.LaunchTimeout), ex.Message);
    }

    [Fact]
    public void LaunchTimeout_DefaultsTo30Seconds()
    {
        // 30 seconds default matches PuppeteerSharp's internal LaunchOptions default.
        Assert.Equal(TimeSpan.FromSeconds(30), new ThemiaPdfOptions().LaunchTimeout);
    }

    [Fact]
    public void ToLaunchOptions_MapsLaunchTimeoutToPuppeteerTimeout()
    {
        var options = new ThemiaPdfOptions
        {
            Headless = true,
            ExecutablePath = "/usr/bin/chromium",
        };
        var args = new[] { "--no-sandbox" };
        var launchTimeout = TimeSpan.FromSeconds(45);

        var launchOptions = PuppeteerPdfRenderer.ToLaunchOptions(options, args, launchTimeout);

        Assert.Equal(45000, launchOptions.Timeout);
        Assert.True(launchOptions.Headless);
        Assert.Equal("/usr/bin/chromium", launchOptions.ExecutablePath);
        Assert.Equal(args, launchOptions.Args);
    }

    [Fact]
    public void RenderTimeout_DefaultsToAFiniteDeadline()
    {
        // Finite on purpose: without a deadline a stuck render holds its concurrency slot for ever.
        Assert.Equal(TimeSpan.FromSeconds(60), new ThemiaPdfOptions().RenderTimeout);
    }

    [Fact]
    public void MaxConcurrency_DefaultsToASmallBound()
    {
        // Deliberately not "unbounded". Every concurrent render opens its own Chromium page, so an
        // unbounded default makes worst-case memory a function of inbound traffic.
        Assert.Equal(2, new ThemiaPdfOptions().MaxConcurrency);
    }

    [Fact]
    public async Task RenderHtmlAsync_AutoDownloadDisabledWithoutExecutablePath_Throws()
    {
        var options = new ThemiaPdfOptions { DisableAutoDownload = true, ExecutablePath = null };
        await using var sut = new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RenderHtmlAsync("<p>hi</p>"));

        Assert.Contains("ExecutablePath", ex.Message);
    }

    [Fact]
    public async Task RenderHtmlAsync_NullHtml_Throws()
    {
        await using var sut = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RenderHtmlAsync(null!));
    }

    [Fact]
    public async Task RenderHtmlAsync_AlreadyCancelledToken_Throws()
    {
        await using var sut = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.RenderHtmlAsync("<p>hi</p>", ct: new CancellationToken(canceled: true)));
    }

    [Fact]
    public void ToPdfOptions_MapsEveryPaperFormat()
    {
        // Literal expectations (not a switch mirroring production) so an identical mis-edit in both
        // places can't hide. Covers all 6 PdfPaperFormat values incl. the A4 default arm.
        Assert.Equal(PaperFormat.A4, Map(PdfPaperFormat.A4));
        Assert.Equal(PaperFormat.A3, Map(PdfPaperFormat.A3));
        Assert.Equal(PaperFormat.Letter, Map(PdfPaperFormat.Letter));
        Assert.Equal(PaperFormat.Legal, Map(PdfPaperFormat.Legal));
        Assert.Equal(PaperFormat.Tabloid, Map(PdfPaperFormat.Tabloid));
        Assert.Equal(PaperFormat.A5, Map(PdfPaperFormat.A5));

        static PaperFormat Map(PdfPaperFormat f) =>
            PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { PaperFormat = f }).Format;
    }

    [Fact]
    public void ToPdfOptions_PrintsPortrait_UnlessLandscapeIsSet()
    {
        Assert.False(PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions()).Landscape);
        Assert.True(PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { PaperFormat = PdfPaperFormat.A5, Landscape = true }).Landscape);
    }

    [Fact]
    public void ToPdfOptions_MapsEachMarginToItsOwnSide()
    {
        // Distinct values catch a copy-paste transposition (e.g. Left ← MarginRight).
        var options = new PdfRenderOptions
        {
            PrintBackground = false,
            MarginTop = "1mm",
            MarginBottom = "2mm",
            MarginLeft = "3mm",
            MarginRight = "4mm",
        };

        var mapped = PuppeteerPdfRenderer.ToPdfOptions(options);

        Assert.False(mapped.PrintBackground);
        Assert.Equal("1mm", mapped.MarginOptions.Top);
        Assert.Equal("2mm", mapped.MarginOptions.Bottom);
        Assert.Equal("3mm", mapped.MarginOptions.Left);
        Assert.Equal("4mm", mapped.MarginOptions.Right);
    }

    [Fact]
    public void ToPdfOptions_KeepsTheLibraryDefaultTagging_WhenTaggedIsNotSet() =>
        Assert.Equal(new PuppeteerSharp.PdfOptions().Tagged, PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions()).Tagged);

    [Fact]
    public void ToPdfOptions_TurnsTaggingOff_WhenTaggedIsFalse() =>
        Assert.False(PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { Tagged = false }).Tagged);

    [Fact]
    public void ToPdfOptions_TurnsTaggingOn_WhenTaggedIsTrue() =>
        Assert.True(PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { Tagged = true }).Tagged);

    [Fact]
    public async Task Dispose_WhenNeverLaunched_IsIdempotentAcrossBothPaths()
    {
        var sut = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        // No browser was ever launched; every disposal path (and repeats) must be a safe no-op.
        await sut.DisposeAsync();
        await sut.DisposeAsync();
        sut.Dispose();
    }
}
