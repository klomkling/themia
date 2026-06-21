using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp.Media;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class PuppeteerPdfRendererTests
{
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
        // places can't hide. Covers all 5 PdfPaperFormat values incl. the A4 default arm.
        Assert.Equal(PaperFormat.A4, Map(PdfPaperFormat.A4));
        Assert.Equal(PaperFormat.A3, Map(PdfPaperFormat.A3));
        Assert.Equal(PaperFormat.Letter, Map(PdfPaperFormat.Letter));
        Assert.Equal(PaperFormat.Legal, Map(PdfPaperFormat.Legal));
        Assert.Equal(PaperFormat.Tabloid, Map(PdfPaperFormat.Tabloid));

        static PaperFormat Map(PdfPaperFormat f) =>
            PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { PaperFormat = f }).Format;
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
    public async Task Dispose_WhenNeverLaunched_IsIdempotentAcrossBothPaths()
    {
        var sut = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        // No browser was ever launched; every disposal path (and repeats) must be a safe no-op.
        await sut.DisposeAsync();
        await sut.DisposeAsync();
        sut.Dispose();
    }
}
