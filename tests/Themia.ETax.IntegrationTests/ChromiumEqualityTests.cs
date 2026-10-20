using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Themia.ETax;
using Themia.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Byte-reproducibility of rendered tax documents on the Chromium this CI downloads. Every case compares the
/// normalised bytes of renders made within one run: a committed golden file would be bound to the build, not the code.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class ChromiumEqualityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ClockGap = TimeSpan.FromSeconds(2.2);
    private static readonly DateOnly IssueDate = new(2026, 10, 14);

    private static ServiceProvider NewBrowserHost(int maxConcurrency = 2)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o =>
        {
            o.MaxConcurrency = maxConcurrency;
            ChromiumPath.Apply(o);
        });
        return services.BuildServiceProvider();
    }

    private static Task<PdfRenderResult> RawRender(IServiceProvider host, TaxDocumentSnapshot snapshot, TaxDocumentMarker marker)
    {
        var html = host.GetRequiredService<TaxDocumentRenderer>().RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, marker);
        return host.GetRequiredService<IVersionedPdfRenderer>().RenderHtmlWithVersionAsync(html, TaxDocumentRenderer.PrintOptions);
    }

    private static Task<TaxDocumentRender> Render(IServiceProvider host, TaxDocumentSnapshot snapshot, TaxDocumentMarker marker) =>
        host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(snapshot, TaxDocumentTemplates.CurrentVersion, marker);

    [Fact]
    public async Task SinglePage_ShouldDifferOnlyInDateFieldsRaw_AndBeIdenticalNormalised_AcrossTimeAndFreshBrowsers()
    {
        var snapshot = Fixtures.Receipt();

        await using var first = NewBrowserHost();
        var raw1 = await RawRender(first, snapshot, TaxDocumentMarker.Original);
        output.WriteLine($"Browser: {raw1.RendererVersion}");
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var raw2 = await RawRender(second, snapshot, TaxDocumentMarker.Original);

        Assert.Equal(raw1.Bytes.Length, raw2.Bytes.Length);
        var dates = PdfDiff.DateValueOffsets(raw1.Bytes);
        Assert.NotEmpty(dates);
        var outside = Enumerable.Range(0, raw1.Bytes.Length)
            .Where(i => raw1.Bytes[i] != raw2.Bytes[i] && !dates.Contains(i)).Take(5).ToArray();
        Assert.True(outside.Length == 0,
            $"Raw renders differ outside /CreationDate and /ModDate at offsets {string.Join(",", outside)} (browser {raw1.RendererVersion}); normalisation would not make them equal.");

        var n1 = TaxDocumentPdfNormaliser.NormaliseDates(raw1.Bytes, IssueDate);
        var n2 = TaxDocumentPdfNormaliser.NormaliseDates(raw2.Bytes, IssueDate);
        PdfDiff.AssertIdentical("single-page", n1, n2, raw1.RendererVersion);
    }

    [Fact]
    public async Task MultiPage_ShouldBeIdentical_WhenTheTableSpansPagesWithARepeatedHeaderAndWatermark()
    {
        var snapshot = Fixtures.Receipt(lines: 150);

        await using var first = NewBrowserHost();
        var a = await Render(first, snapshot, TaxDocumentMarker.CancelledCopy);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await Render(second, snapshot, TaxDocumentMarker.CancelledCopy);

        output.WriteLine($"Browser: {a.RendererVersion}; pages: {PdfDiff.PageCount(a.Pdf)}");
        Assert.True(PdfDiff.PageCount(a.Pdf) >= 3, $"Expected at least 3 pages, got {PdfDiff.PageCount(a.Pdf)}.");
        PdfDiff.AssertIdentical("multi-page", a.Pdf, b.Pdf, a.RendererVersion);
        Assert.Equal(a.PdfSha256, b.PdfSha256);
    }

    [Fact]
    public async Task Images_ShouldBeIdentical_WhenPngJpegAndSvgAreInlined()
    {
        var png = Fixtures.DataUri("image/png", Fixtures.Resource("Fixtures.logo.png"));
        var jpeg = Fixtures.DataUri("image/jpeg", Fixtures.Resource("Fixtures.logo.jpg"));
        var html = $"""
            <html><head><meta charset="utf-8"><title>images</title></head><body>
            <img src="{png}" width="96" height="96"> <img src="{jpeg}" width="96" height="96">
            <svg width="120" height="120" viewBox="0 0 120 120"><defs><linearGradient id="g"><stop offset="0" stop-color="#c00"/><stop offset="1" stop-color="#06c"/></linearGradient></defs>
            <circle cx="60" cy="60" r="50" fill="url(#g)"/><text x="30" y="65" font-size="18">Themia</text></svg>
            </body></html>
            """;

        await using var first = NewBrowserHost();
        var a = await first.GetRequiredService<IVersionedPdfRenderer>().RenderHtmlWithVersionAsync(html, TaxDocumentRenderer.PrintOptions);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await second.GetRequiredService<IVersionedPdfRenderer>().RenderHtmlWithVersionAsync(html, TaxDocumentRenderer.PrintOptions);

        var raw = Fixtures.Latin1(a.Bytes);
        Assert.Contains("/Subtype /Image", raw);
        PdfDiff.AssertIdentical(
            "images",
            TaxDocumentPdfNormaliser.NormaliseDates(a.Bytes, IssueDate),
            TaxDocumentPdfNormaliser.NormaliseDates(b.Bytes, IssueDate),
            a.RendererVersion);
    }

    [Fact]
    public async Task Concurrency_ShouldMatchSequentialRenders_WhenFourDocumentsRenderAtOnce()
    {
        var snapshots = Enumerable.Range(1, 4)
            .Select(i => Fixtures.Receipt($"TAX-2026-00000{i}", lines: i * 20, amount: 10_000 * i)).ToArray();

        await using var sequentialHost = NewBrowserHost(maxConcurrency: 1);
        var sequential = new List<TaxDocumentRender>();
        foreach (var s in snapshots)
            sequential.Add(await Render(sequentialHost, s, TaxDocumentMarker.Original));

        await using var parallelHost = NewBrowserHost(maxConcurrency: 2);
        var concurrent = await Task.WhenAll(snapshots.Select(s => Render(parallelHost, s, TaxDocumentMarker.Original)));

        for (var i = 0; i < snapshots.Length; i++)
            PdfDiff.AssertIdentical($"concurrency-{i}", sequential[i].Pdf, concurrent[i].Pdf, sequential[i].RendererVersion);
    }

    [Fact]
    public async Task Markers_ShouldDifferFromEachOther_AndEachBeStableAcrossRenders()
    {
        var snapshot = Fixtures.Receipt();
        await using var host = NewBrowserHost();
        var hashes = new Dictionary<TaxDocumentMarker, string>();

        foreach (var marker in Enum.GetValues<TaxDocumentMarker>())
        {
            var a = await Render(host, snapshot, marker);
            await Task.Delay(ClockGap);
            var b = await Render(host, snapshot, marker);
            PdfDiff.AssertIdentical($"marker-{marker}", a.Pdf, b.Pdf, a.RendererVersion);
            hashes[marker] = a.PdfSha256;
        }

        Assert.Equal(hashes.Count, hashes.Values.Distinct().Count());
    }

    [Fact]
    public async Task RendererVersion_ShouldBeEqualAcrossBrowsers_AndMatchTheProducerMajor()
    {
        await using var first = NewBrowserHost();
        await using var second = NewBrowserHost();
        var a = await RawRender(first, Fixtures.Receipt(), TaxDocumentMarker.Original);
        var b = await RawRender(second, Fixtures.Receipt(), TaxDocumentMarker.Original);

        output.WriteLine($"Browser: {a.RendererVersion}");
        Assert.Equal(a.RendererVersion, b.RendererVersion);
        var producer = ProducerMajor().Match(Fixtures.Latin1(a.Bytes));
        var browser = BrowserMajor().Match(a.RendererVersion);
        Assert.True(producer.Success && browser.Success, $"Cannot read majors from Producer / '{a.RendererVersion}'.");
        Assert.Equal(browser.Groups[1].Value, producer.Groups[1].Value);
    }

    [Fact]
    public async Task ZeroWidthDefaultIgnorables_ShouldNotPullInAFallbackFont_AndStayIdentical()
    {
        var plain = Fixtures.Receipt();
        var pasted = plain with
        {
            Buyer = plain.Buyer with { Name = "คุณ\u200Bสมชาย\u200C ใจ\u200Dดี\u2060\uFEFF" },
        };

        await using var first = NewBrowserHost();
        var a = await Render(first, pasted, TaxDocumentMarker.Original);
        var reference = await Render(first, plain, TaxDocumentMarker.Original);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await Render(second, pasted, TaxDocumentMarker.Original);

        PdfDiff.AssertIdentical("ignorables", a.Pdf, b.Pdf, a.RendererVersion);
        var fonts = (byte[] pdf) => Regex.Matches(Fixtures.Latin1(pdf), "/BaseFont").Count;
        Assert.Equal(fonts(reference.Pdf), fonts(a.Pdf));
    }

    [GeneratedRegex(@"Skia/PDF m(\d+)")]
    private static partial Regex ProducerMajor();

    [GeneratedRegex(@"/(\d+)\.")]
    private static partial Regex BrowserMajor();
}
