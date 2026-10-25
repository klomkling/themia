using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PuppeteerSharp;
using Themia.ETax;
using Xunit;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Template versions "4" (A4 portrait) and "5" (A5 landscape) on the Chromium this CI downloads: the page counts the layout constants promise are the page
/// counts the browser prints (a count that no longer fits would spill a line onto an extra page), each page has its version's paper size, and a render is
/// byte-reproducible.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class TemplateVersion4ChromiumTests
{
    private static ServiceProvider NewHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => ChromiumPath.Apply(o));
        return services.BuildServiceProvider();
    }

    internal static TaxDocumentSnapshot ReceiptFor(int lines, long withheld) => Receipt(lines, withheld);

    internal static TaxDocumentSnapshot CreditNoteFor(int lines) => CreditNote(lines);

    private static TaxDocumentSnapshot Receipt(int lines, long withheld = 0)
    {
        var rows = Enumerable.Range(1, lines).Select(i => new TaxDocumentLine
        {
            Description = $"รายการที่ {i}: ค่าบริการตัวอย่าง Café Łódź",
            AmountExVatSatang = 100_000,
            Quantity = 1m,
            Unit = "ชิ้น",
            UnitPriceSatang = 100_000,
        }).ToArray();
        var exVat = rows.Sum(r => r.AmountExVatSatang);
        var vat = VatMath.AddVat(exVat, 700);
        return new TaxDocumentSnapshot
        {
            Kind = TaxDocumentKind.TaxInvoiceReceipt,
            DocumentNumber = "RTX-6910-000001",
            IssueDate = new DateOnly(2026, 10, 25),
            Seller = new TaxParty { Name = "บริษัท ตัวอย่าง จำกัด", TaxId = "0105536078673", Branch = "00000", Address = "99/9 ถนนตัวอย่าง แขวงตัวอย่าง เขตตัวอย่าง กรุงเทพมหานคร 10000" },
            Buyer = new TaxParty { Name = "บริษัท ลูกค้าตัวอย่าง จำกัด", TaxId = "1101700230708", Branch = "00000", Address = "1/1 ถนนตัวอย่าง แขวงตัวอย่าง เขตตัวอย่าง กรุงเทพมหานคร 10000" },
            Lines = rows,
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = exVat,
                VatSatang = vat.VatSatang,
                InclVatSatang = vat.InclVatSatang,
                WithheldSatang = withheld,
                NetReceivedSatang = vat.InclVatSatang - withheld,
                VatRateBasisPoints = 700,
            },
            Payment = new TaxPayment { Method = TaxPaymentMethod.Transfer, Date = new DateOnly(2026, 10, 25) },
        };
    }

    private static TaxDocumentSnapshot CreditNote(int lines)
    {
        var rows = Enumerable.Range(1, lines).Select(i => new TaxDocumentLine
        {
            Description = $"รายการที่ {i}: ค่าอบรมการใช้งาน Café Łódź",
            AmountExVatSatang = 100_000,
            OriginalAmountExVatSatang = 150_000,
            CorrectAmountExVatSatang = 50_000,
        }).ToArray();
        var exVat = rows.Sum(r => r.AmountExVatSatang);
        var vat = VatMath.AddVat(exVat, 700);
        return Receipt(1) with
        {
            Kind = TaxDocumentKind.CreditNote,
            DocumentNumber = "CN-6910-000001",
            Lines = rows,
            Payment = null,
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = exVat,
                VatSatang = vat.VatSatang,
                InclVatSatang = vat.InclVatSatang,
                WithheldSatang = 0,
                NetReceivedSatang = vat.InclVatSatang,
                VatRateBasisPoints = 700,
            },
            Note = new TaxNoteReference
            {
                OriginalDocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                OriginalDocumentNumber = "RTX-6910-000001",
                OriginalIssueDate = new DateOnly(2026, 10, 25),
                Mode = TaxNoteMode.Adjustment,
                Reason = "ลดราคาค่าอบรมตามที่ตกลงกัน",
                ReasonKind = TaxNoteReason.PriceReduction,
                OriginalValueSatang = vat.InclVatSatang * 3,
                CorrectedValueSatang = vat.InclVatSatang * 2,
            },
        };
    }

    [GeneratedRegex(@"/Type\s*/Page[^s]")]
    private static partial Regex PageObject();

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]")]
    private static partial Regex MediaBox();

    private static int Pages(byte[] pdf) => PageObject().Matches(System.Text.Encoding.Latin1.GetString(pdf)).Count;

    private static TaxDocumentPageSize SizeOf(string version) => TaxDocumentTemplates.PageSizeOf(version)!.Value;

    private static int[] LineCounts(string version, TaxDocumentKind kind)
    {
        var size = SizeOf(version);
        var single = TaxDocumentPageLayout.SinglePageRows(size, kind);
        var middle = TaxDocumentPageLayout.MiddlePageRows(size, kind);
        var last = TaxDocumentPageLayout.LastPageRows(size, kind);
        return [1, single, single + 1, single + last, single + last + 1, single + middle + last, single + middle + last + 1];
    }

    public static TheoryData<string, TaxDocumentKind, int> Cases()
    {
        var data = new TheoryData<string, TaxDocumentKind, int>();
        foreach (var version in new[] { "4", "5" })
        {
            foreach (var kind in new[] { TaxDocumentKind.TaxInvoiceReceipt, TaxDocumentKind.CreditNote })
            {
                foreach (var n in LineCounts(version, kind))
                    data.Add(version, kind, n);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Render_ShouldPrintExactlyThePlannedNumberOfPages_OnTheVersionsPaper(string version, TaxDocumentKind kind, int lines)
    {
        await using var host = NewHost();
        var snapshot = kind == TaxDocumentKind.CreditNote ? CreditNote(lines) : Receipt(lines);

        var render = await host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(snapshot, version, TaxDocumentMarker.Original);

        Assert.Equal(TaxDocumentPageLayout.Plan(SizeOf(version), kind, lines).Count, Pages(render.Pdf));
        var box = MediaBox().Match(System.Text.Encoding.Latin1.GetString(render.Pdf));
        Assert.True(box.Success);
        var width = double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(width, 595, 596.5);
        if (version == "4")
            Assert.InRange(height, 841, 843);
        else
            Assert.InRange(height, 419, 421);
    }

    // The watermark sits behind the content (z-index -1), so anything opaque painted on html or body would cover it and a cancelled copy would print
    // without it while its HTML still contains the words. Only what the browser paints can show that: a cancelled copy and a plain copy have the same
    // marker text, so their screenshots differ exactly when the watermark is visible.
    private static async Task<byte[]> ScreenshotAsync(string html)
    {
        var path = Environment.GetEnvironmentVariable(ChromiumPath.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            path = (await new BrowserFetcher().DownloadAsync().ConfigureAwait(false)).GetExecutablePath();

        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = path, Args = ["--no-sandbox"] });
        await using var page = await browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 794, Height = 1123 });
        await page.SetContentAsync(html);
        return await page.ScreenshotDataAsync();
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    public async Task Render_ShouldPaintTheCancelledWatermark_OnACancelledCopy(string version)
    {
        await using var host = NewHost();
        var renderer = host.GetRequiredService<TaxDocumentRenderer>();
        var receipt = Receipt(2);

        var copy = await ScreenshotAsync(renderer.RenderHtml(receipt, version, TaxDocumentMarker.Copy));
        var cancelled = await ScreenshotAsync(renderer.RenderHtml(receipt, version, TaxDocumentMarker.CancelledCopy));

        Assert.NotEqual(copy, cancelled);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    public async Task Render_ShouldKeepAFullDebitNoteOnOnePage_AtTheCreditNoteCapacity(string version)
    {
        await using var host = NewHost();
        var lines = TaxDocumentPageLayout.SinglePageRows(SizeOf(version), TaxDocumentKind.DebitNote);
        var debit = CreditNote(lines) with
        {
            Kind = TaxDocumentKind.DebitNote,
            DocumentNumber = "DN-6910-000001",
            Note = CreditNote(lines).Note! with { ReasonKind = null, Reason = "ปรับราคาตามจำนวนที่ส่งจริง" },
        };

        var render = await host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(debit, version, TaxDocumentMarker.Original);

        Assert.Equal(1, Pages(render.Pdf));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    public async Task Render_ShouldKeepAFullReceiptWithWithholdingRowsOnOnePage(string version)
    {
        await using var host = NewHost();
        var single = TaxDocumentPageLayout.SinglePageRows(SizeOf(version), TaxDocumentKind.TaxInvoiceReceipt);

        var render = await host.GetRequiredService<TaxDocumentRenderer>()
            .RenderAsync(Receipt(single, withheld: 3_000), version, TaxDocumentMarker.Original);

        Assert.Equal(1, Pages(render.Pdf));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    public async Task Render_ShouldBeByteIdentical_ForTheSameDocument(string version)
    {
        await using var host = NewHost();
        var renderer = host.GetRequiredService<TaxDocumentRenderer>();

        var first = await renderer.RenderAsync(Receipt(8), version, TaxDocumentMarker.Copy);
        var second = await renderer.RenderAsync(Receipt(8), version, TaxDocumentMarker.Copy);

        Assert.Equal(first.PdfSha256, second.PdfSha256);
    }
}
