using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Themia.ETax.IntegrationTests;

/// <summary>Byte-reproducibility of template version 2 (template 1 plus the amount in Thai words) on the Chromium this CI downloads.</summary>
[Trait("Category", "Integration")]
public sealed class TemplateVersion2ChromiumTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ClockGap = TimeSpan.FromSeconds(2.2);

    private static ServiceProvider NewBrowserHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o =>
        {
            o.MaxConcurrency = 2;
            ChromiumPath.Apply(o);
        });
        services.ConfigureThemiaETaxTemplates(o => { o.TemplateVersion = "3"; });
        return services.BuildServiceProvider();
    }

    private static Task<TaxDocumentRender> Render(IServiceProvider host, TaxDocumentSnapshot snapshot, string version, TaxDocumentMarker marker) =>
        host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(snapshot, version, marker);

    [Fact]
    public async Task SinglePage_ShouldBeIdentical_AcrossTimeAndFreshBrowsers_AndStayOnOnePage()
    {
        var snapshot = Fixtures.Receipt();

        await using var first = NewBrowserHost();
        var a = await Render(first, snapshot, "2", TaxDocumentMarker.Original);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await Render(second, snapshot, "2", TaxDocumentMarker.Original);

        output.WriteLine($"Browser: {a.RendererVersion}; pages: {PdfDiff.PageCount(a.Pdf)}");
        Assert.Equal(1, PdfDiff.PageCount(a.Pdf));
        PdfDiff.AssertIdentical("v2-single-page", a.Pdf, b.Pdf, a.RendererVersion);
    }

    [Fact]
    public async Task MultiPage_ShouldBeIdentical_WhenTheAmountInWordsFollowsATableThatSpansPages()
    {
        var snapshot = Fixtures.Receipt(lines: 150);

        await using var first = NewBrowserHost();
        var a = await Render(first, snapshot, "2", TaxDocumentMarker.CancelledCopy);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await Render(second, snapshot, "2", TaxDocumentMarker.CancelledCopy);

        Assert.True(PdfDiff.PageCount(a.Pdf) >= 3, $"Expected at least 3 pages, got {PdfDiff.PageCount(a.Pdf)}.");
        PdfDiff.AssertIdentical("v2-multi-page", a.Pdf, b.Pdf, a.RendererVersion);
    }

    [Fact]
    public async Task Version2_ShouldRenderDifferentBytesFromVersion1_AndVersion1ShouldStayStable()
    {
        var snapshot = Fixtures.Receipt();
        await using var host = NewBrowserHost();

        var one = await Render(host, snapshot, "1", TaxDocumentMarker.Original);
        var two = await Render(host, snapshot, "2", TaxDocumentMarker.Original);
        await Task.Delay(ClockGap);
        var oneAgain = await Render(host, snapshot, "1", TaxDocumentMarker.Original);

        Assert.NotEqual(one.PdfSha256, two.PdfSha256);
        PdfDiff.AssertIdentical("v1-after-v2", one.Pdf, oneAgain.Pdf, one.RendererVersion);
    }
}
