using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Themia.ETax;
using Xunit;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Template version "4" on the Chromium this CI downloads: the page counts the layout constants promise are the page counts the browser prints (a count
/// that no longer fits would spill a line onto an extra page), each page is A5 landscape, and a render is byte-reproducible.
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

    private static int[] LineCounts(TaxDocumentKind kind)
    {
        var single = A5Layout.SinglePageRows(kind);
        var middle = A5Layout.MiddlePageRows(kind);
        var last = A5Layout.LastPageRows(kind);
        return [1, single, single + 1, single + last, single + last + 1, single + middle + last, single + middle + last + 1];
    }

    public static TheoryData<TaxDocumentKind, int> Cases()
    {
        var data = new TheoryData<TaxDocumentKind, int>();
        foreach (var kind in new[] { TaxDocumentKind.TaxInvoiceReceipt, TaxDocumentKind.CreditNote })
        {
            foreach (var n in LineCounts(kind))
                data.Add(kind, n);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Render_ShouldPrintExactlyThePlannedNumberOfA5LandscapePages(TaxDocumentKind kind, int lines)
    {
        await using var host = NewHost();
        var snapshot = kind == TaxDocumentKind.CreditNote ? CreditNote(lines) : Receipt(lines);

        var render = await host.GetRequiredService<TaxDocumentRenderer>().RenderAsync(snapshot, "4", TaxDocumentMarker.Original);

        Assert.Equal(A5Layout.Plan(kind, lines).Count, Pages(render.Pdf));
        var box = MediaBox().Match(System.Text.Encoding.Latin1.GetString(render.Pdf));
        Assert.True(box.Success);
        Assert.InRange(double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 595, 596.5);
        Assert.InRange(double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 419, 421);
    }

    [Fact]
    public async Task Render_ShouldKeepAFullReceiptWithWithholdingRowsOnOnePage()
    {
        await using var host = NewHost();

        var render = await host.GetRequiredService<TaxDocumentRenderer>()
            .RenderAsync(Receipt(A5Layout.SinglePageRows(TaxDocumentKind.TaxInvoiceReceipt), withheld: 3_000), "4", TaxDocumentMarker.Original);

        Assert.Equal(1, Pages(render.Pdf));
    }

    [Fact]
    public async Task Render_ShouldBeByteIdentical_ForTheSameDocument()
    {
        await using var host = NewHost();
        var renderer = host.GetRequiredService<TaxDocumentRenderer>();

        var first = await renderer.RenderAsync(Receipt(8), "4", TaxDocumentMarker.Copy);
        var second = await renderer.RenderAsync(Receipt(8), "4", TaxDocumentMarker.Copy);

        Assert.Equal(first.PdfSha256, second.PdfSha256);
    }
}
