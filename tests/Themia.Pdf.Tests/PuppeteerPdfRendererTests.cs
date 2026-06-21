using Microsoft.Extensions.Logging.Abstractions;
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
}
