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

    [Theory]
    [InlineData(PdfPaperFormat.A4)]
    [InlineData(PdfPaperFormat.A3)]
    [InlineData(PdfPaperFormat.Letter)]
    [InlineData(PdfPaperFormat.Legal)]
    [InlineData(PdfPaperFormat.Tabloid)]
    public void ToPdfOptions_MapsEveryPaperFormat(PdfPaperFormat format)
    {
        var expected = format switch
        {
            PdfPaperFormat.A3 => PaperFormat.A3,
            PdfPaperFormat.Letter => PaperFormat.Letter,
            PdfPaperFormat.Legal => PaperFormat.Legal,
            PdfPaperFormat.Tabloid => PaperFormat.Tabloid,
            _ => PaperFormat.A4,
        };

        var mapped = PuppeteerPdfRenderer.ToPdfOptions(new PdfRenderOptions { PaperFormat = format });

        Assert.Equal(expected, mapped.Format);
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
