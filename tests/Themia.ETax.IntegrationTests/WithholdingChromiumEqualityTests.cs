using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Themia.ETax.Withholding;
using Themia.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Byte-reproducibility of rendered withholding certificates on the Chromium this CI downloads, and the layout property the official form
/// needs: one page. Every case compares normalised bytes of renders made within one run (a committed golden file would be bound to the build).
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class WithholdingChromiumEqualityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ClockGap = TimeSpan.FromSeconds(2.2);
    private static readonly DateOnly IssueDate = new(2026, 10, 15);

    private static ServiceProvider NewBrowserHost(int maxConcurrency = 2)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxWithholdingRendering(o => o.MaxConcurrency = maxConcurrency);
        return services.BuildServiceProvider();
    }

    private static Task<TaxDocumentRender> Render(
        IServiceProvider host, WithholdingCertificateSnapshot snapshot,
        WithholdingCopy copy = WithholdingCopy.Copy1, WithholdingRenderMode mode = WithholdingRenderMode.Issued) =>
        host.GetRequiredService<WithholdingCertificateRenderer>().RenderAsync(snapshot, WithholdingTemplates.CurrentVersion, copy, mode);

    private static Task<PdfRenderResult> RawRender(IServiceProvider host, WithholdingCertificateSnapshot snapshot)
    {
        var html = host.GetRequiredService<WithholdingCertificateRenderer>()
            .RenderHtml(snapshot, WithholdingTemplates.CurrentVersion, WithholdingCopy.Copy1, WithholdingRenderMode.Issued);
        return host.GetRequiredService<IVersionedPdfRenderer>().RenderHtmlWithVersionAsync(html, WithholdingCertificateRenderer.PrintOptions);
    }

    [Fact]
    public async Task SinglePage_ShouldDifferOnlyInDateFieldsRaw_AndBeIdenticalNormalised_AcrossTimeAndFreshBrowsers()
    {
        var snapshot = WithholdingFixtures.Single();

        await using var first = NewBrowserHost();
        var raw1 = await RawRender(first, snapshot);
        output.WriteLine($"Browser: {raw1.RendererVersion}");
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var raw2 = await RawRender(second, snapshot);

        Assert.Equal(raw1.Bytes.Length, raw2.Bytes.Length);
        var dates = PdfDiff.DateValueOffsets(raw1.Bytes);
        Assert.NotEmpty(dates);
        var outside = Enumerable.Range(0, raw1.Bytes.Length)
            .Where(i => raw1.Bytes[i] != raw2.Bytes[i] && !dates.Contains(i)).Take(5).ToArray();
        Assert.True(outside.Length == 0,
            $"Raw renders differ outside /CreationDate and /ModDate at offsets {string.Join(",", outside)} (browser {raw1.RendererVersion}); normalisation would not make them equal.");

        PdfDiff.AssertIdentical(
            "withholding-single",
            TaxDocumentPdfNormaliser.NormaliseDates(raw1.Bytes, IssueDate),
            TaxDocumentPdfNormaliser.NormaliseDates(raw2.Bytes, IssueDate),
            raw1.RendererVersion);
    }

    [Fact]
    public async Task EveryLineFilled_ShouldStillFitOnePage_AndBeIdenticalAcrossFreshBrowsers()
    {
        var snapshot = WithholdingFixtures.Full();

        await using var first = NewBrowserHost();
        var a = await Render(first, snapshot, WithholdingCopy.Copy2);
        await Task.Delay(ClockGap);
        await using var second = NewBrowserHost();
        var b = await Render(second, snapshot, WithholdingCopy.Copy2);

        output.WriteLine($"Browser: {a.RendererVersion}; pages: {PdfDiff.PageCount(a.Pdf)}");
        Assert.Equal(1, PdfDiff.PageCount(a.Pdf));
        PdfDiff.AssertIdentical("withholding-full", a.Pdf, b.Pdf, a.RendererVersion);
        Assert.Equal(a.PdfSha256, b.PdfSha256);
    }

    [Fact]
    public async Task Markings_ShouldDifferFromEachOther_AndEachBeStableAcrossRenders()
    {
        var snapshot = WithholdingFixtures.Single();
        await using var host = NewBrowserHost();
        var hashes = new List<string>();

        foreach (var (copy, mode) in new[]
                 {
                     (WithholdingCopy.Copy1, WithholdingRenderMode.Issued),
                     (WithholdingCopy.Copy2, WithholdingRenderMode.Issued),
                     (WithholdingCopy.Copy1, WithholdingRenderMode.ReplacementCopy),
                     (WithholdingCopy.Copy2, WithholdingRenderMode.Cancelled),
                 })
        {
            var a = await Render(host, snapshot, copy, mode);
            await Task.Delay(ClockGap);
            var b = await Render(host, snapshot, copy, mode);
            Assert.Equal(1, PdfDiff.PageCount(a.Pdf));
            PdfDiff.AssertIdentical($"marking-{copy}-{mode}", a.Pdf, b.Pdf, a.RendererVersion);
            hashes.Add(a.PdfSha256);
        }

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public async Task Concurrency_ShouldMatchSequentialRenders_WhenFourCertificatesRenderAtOnce()
    {
        var snapshots = new[]
        {
            WithholdingFixtures.Single("WHT-2569-000001"),
            WithholdingFixtures.Full("WHT-2569-000002"),
            WithholdingFixtures.Single("WHT-2569-000003"),
            WithholdingFixtures.Full("WHT-2569-000004"),
        };

        await using var sequentialHost = NewBrowserHost(maxConcurrency: 1);
        var sequential = new List<TaxDocumentRender>();
        foreach (var s in snapshots)
            sequential.Add(await Render(sequentialHost, s));

        await using var parallelHost = NewBrowserHost(maxConcurrency: 2);
        var concurrent = await Task.WhenAll(snapshots.Select(s => Render(parallelHost, s)));

        for (var i = 0; i < snapshots.Length; i++)
            PdfDiff.AssertIdentical($"withholding-concurrency-{i}", sequential[i].Pdf, concurrent[i].Pdf, sequential[i].RendererVersion);
    }

    [Fact]
    public async Task ThaiAndLatinExtendedText_ShouldPrintInTheEmbeddedFontOnly()
    {
        await using var host = NewBrowserHost();
        var render = await Render(host, WithholdingFixtures.Full());

        var fonts = Regex.Matches(Fixtures.Latin1(render.Pdf), @"/BaseFont\s*/([^\s/>\]]+)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        output.WriteLine(string.Join(", ", fonts));
        Assert.NotEmpty(fonts);
        Assert.All(fonts, f => Assert.Contains("Sarabun", f, StringComparison.Ordinal));
    }
}
