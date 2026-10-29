using Microsoft.Extensions.DependencyInjection;
using PuppeteerSharp;
using Themia.ETax;
using Xunit;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Templates "6" (A4 portrait) and "7" (A5 landscape) print five payment boxes beside the fixed-width receiver signature. The string tests cannot see the
/// page, so this one asks the browser: with every method the payment line must end before the signature starts, the signature must stay inside the page,
/// and the receipt must still print on the pages the layout plans for.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TemplateVersion6ChromiumTests
{
    private const string Geometry = """
        (() => {
            const r = s => document.querySelector(s).getBoundingClientRect();
            const pay = r('.pay'), sig = r('.sig');
            return JSON.stringify({ payRight: pay.right, sigLeft: sig.left, sigRight: sig.right, width: document.documentElement.clientWidth });
        })()
        """;

    public static TheoryData<string, TaxPaymentMethod> Cases()
    {
        var data = new TheoryData<string, TaxPaymentMethod>();
        foreach (var version in new[] { "6", "7" })
            foreach (var method in Enum.GetValues<TaxPaymentMethod>())
                data.Add(version, method);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PaymentLine_ShouldEndBeforeTheSignature_AndTheSignatureShouldStayOnThePage(string version, TaxPaymentMethod method)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => ChromiumPath.Apply(o));
        await using var host = services.BuildServiceProvider();
        var snapshot = TemplateVersion4ChromiumTests.ReceiptFor(1, 0) with { Payment = new TaxPayment { Method = method, Date = new DateOnly(2026, 10, 25) } };
        var html = host.GetRequiredService<TaxDocumentRenderer>().RenderHtml(snapshot, version, TaxDocumentMarker.Original);

        var path = Environment.GetEnvironmentVariable(ChromiumPath.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            path = (await new BrowserFetcher().DownloadAsync()).GetExecutablePath();
        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = path, Args = ["--no-sandbox"] });
        await using var page = await browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 794, Height = version == "6" ? 1123 : 559 });
        await page.SetContentAsync(html);
        var json = await page.EvaluateExpressionAsync<string>(Geometry);
        var box = System.Text.Json.JsonDocument.Parse(json).RootElement;

        Assert.True(box.GetProperty("payRight").GetDouble() <= box.GetProperty("sigLeft").GetDouble() + 0.5, json);
        Assert.True(box.GetProperty("sigRight").GetDouble() <= box.GetProperty("width").GetDouble() + 0.5, json);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("7")]
    public async Task Receipt_ShouldPrintOnThePlannedPages_WithTheLongerPaymentLine(string version)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => ChromiumPath.Apply(o));
        await using var host = services.BuildServiceProvider();
        var size = TaxDocumentTemplates.PageSizeOf(version)!.Value;
        var snapshot = TemplateVersion4ChromiumTests.ReceiptFor(1, 0) with { Payment = new TaxPayment { Method = TaxPaymentMethod.MobileBanking, Date = new DateOnly(2026, 10, 25) } };

        var render = await host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(snapshot, version, TaxDocumentMarker.Original);

        var pages = System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(render.Pdf), @"/Type\s*/Page[^s]").Count;
        Assert.Equal(TaxDocumentPageLayout.Plan(size, TaxDocumentKind.TaxInvoiceReceipt, 1).Count, pages);
    }
}
