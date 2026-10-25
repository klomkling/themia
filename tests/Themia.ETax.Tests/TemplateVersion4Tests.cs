using System.Net;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template versions "4" (A4 portrait) and "5" (A5 landscape): the combined receipt/tax invoice and the credit note. They share one layout, so the printed
/// words and numbers are read from the rendered HTML of either; <see cref="TemplateVersion4GoldenTests"/> pins the whole HTML of both.
/// </summary>
public sealed class TemplateVersion4Tests
{
    private static string Html(TaxDocumentSnapshot s, TaxDocumentMarker marker = TaxDocumentMarker.Original, TaxDocumentLogo? logo = null, string version = "4") =>
        WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(s, version, marker, logo));

    internal static TaxDocumentSnapshot Receipt(int lines)
    {
        var rows = Enumerable.Range(1, lines).Select(i => new TaxDocumentLine
        {
            Description = $"รายการที่ {i}",
            AmountExVatSatang = 100_000,
            Quantity = 1m,
            Unit = "ชิ้น",
            UnitPriceSatang = 100_000,
        }).ToArray();
        var exVat = rows.Sum(r => r.AmountExVatSatang);
        var split = VatMath.Split(exVat * 107 / 100, 700);
        return Samples.Receipt() with
        {
            Lines = rows,
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = split.ExVatSatang,
                VatSatang = split.VatSatang,
                InclVatSatang = exVat * 107 / 100,
                WithheldSatang = 0,
                NetReceivedSatang = exVat * 107 / 100,
                VatRateBasisPoints = 700,
            },
            Payment = new TaxPayment { Method = TaxPaymentMethod.Transfer, Date = new DateOnly(2026, 10, 14) },
        };
    }

    [Fact]
    public void Receipt_ShouldPrintEveryRequiredItem()
    {
        var html = Html(Receipt(2));

        Assert.Contains("ใบเสร็จรับเงิน/ใบกำกับภาษี", html);
        Assert.Contains("Receipt/Tax Invoice", html);
        Assert.Contains("บริษัท ตัวอย่าง จำกัด", html);
        Assert.Contains("0 1055 36078 67 3", html);
        Assert.Contains("สำนักงานใหญ่", html);
        Assert.Contains("คุณสมชาย ใจดี", html);
        Assert.Contains("1 1017 00230 70 8", html);
        Assert.Contains("TAX-2026-000001", html);
        Assert.Contains("14/10/2569", html);
        Assert.Contains("1,000.00", html);
        Assert.Contains("ภาษีมูลค่าเพิ่ม 7%", html);
        Assert.Contains("ต้นฉบับ", html);
        Assert.Contains("ผู้รับเงิน", html);
        Assert.Contains("สองพันหนึ่งร้อยสี่สิบบาทถ้วน", html);
    }

    [Fact]
    public void Receipt_ShouldMarkACopy_AndOmitTheBuyersTaxLinesWhenUnknown()
    {
        var s = Receipt(1) with { Buyer = new TaxParty { Name = "ลูกค้าทั่วไป" } };

        var html = Html(s, TaxDocumentMarker.Copy);

        Assert.Contains("สำเนา", html);
        Assert.DoesNotContain("ต้นฉบับ", html);
        Assert.Equal(1, html.Split("เลขประจำตัวผู้เสียภาษี").Length - 1);
    }

    [Fact]
    public void Receipt_ShouldTickThePaymentMethod_AndPrintItsDate()
    {
        var html = Html(Receipt(1));

        Assert.Contains("<span class=\"cb\"></span> เงินสด", html);
        Assert.Contains("<span class=\"cb\">✓</span> โอนเงิน", html);
        Assert.Contains("<span class=\"date\">14/10/2569</span>", html);
    }

    [Fact]
    public void Receipt_ShouldPrintWithholdingRows_OnlyWhenTaxWasWithheld()
    {
        Assert.DoesNotContain("หักภาษี ณ ที่จ่าย", Html(Receipt(1)));

        var withheld = Receipt(1) with { Totals = Receipt(1).Totals with { WithheldSatang = 3_000, NetReceivedSatang = 104_000 } };
        Assert.Contains("หักภาษี ณ ที่จ่าย", Html(withheld));
    }

    [Fact]
    public void Receipt_ShouldPrintNoLogoBox_WhenThereIsNoLogo_AndTheImageWhenThereIs()
    {
        Assert.DoesNotContain("class=\"logo\"", Html(Receipt(1)));

        var png = new TaxDocumentLogo([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        Assert.Contains("class=\"logo\"><img src=\"data:image/png;base64,", Html(Receipt(1), logo: png));
    }

    [Fact]
    public void Receipt_ShouldContinueOnASecondPage_WithCarriedAndBroughtForwardSubtotals_OnA5()
    {
        var html = Html(Receipt(7), version: "5");

        Assert.Equal(2, html.Split("<section class=\"page\"").Length - 1);
        Assert.Contains("ยอดยกไป", html);
        Assert.Contains("6,000.00", html);
        Assert.Contains("ยอดยกมา", html);
        Assert.Contains("ต่อหน้าถัดไป", html);
        Assert.Contains("หน้า 1/2", html);
        Assert.Contains("หน้า 2/2", html);
        Assert.Contains("(ต่อ / continued)", html);
        Assert.Equal(1, html.Split("ผู้รับเงิน").Length - 1);
    }

    [Fact]
    public void Receipt_ShouldFlowFromTheTopOfAnA4Page_WithoutBlankRowsOrAPageBreak()
    {
        var html = Html(Receipt(7));

        Assert.Equal(1, html.Split("<section class=\"page\"").Length - 1);
        Assert.DoesNotContain("ยอดยกไป", html);
        Assert.Equal(0, html.Split("<tr><td></td><td></td><td></td><td></td><td></td></tr>").Length - 1);
        Assert.Contains("@page { size: 210mm 297mm;", html);
        Assert.Contains("min-height: 297mm;", html);
    }

    [Fact]
    public void Receipt_ShouldPadASmallA5FormToItsFullHeight_AndPrintItsPageSize()
    {
        var html = Html(Receipt(3), version: "5");

        Assert.Equal(3, html.Split("<tr><td></td><td></td><td></td><td></td><td></td></tr>").Length - 1);
        Assert.Contains("@page { size: 210mm 148mm;", html);
    }

    [Fact]
    public void Receipt_ShouldContinueOnASecondA4Page_OnlyWhenTheFirstIsFull()
    {
        var single = TaxDocumentPageLayout.SinglePageRows(TaxDocumentPageSize.A4Portrait, TaxDocumentKind.TaxInvoiceReceipt);

        Assert.Equal(1, Html(Receipt(single)).Split("<section class=\"page\"").Length - 1);

        var two = Html(Receipt(single + 1));
        Assert.Equal(2, two.Split("<section class=\"page\"").Length - 1);
        Assert.Contains("ยอดยกไป", two);
        Assert.Contains("ยอดยกมา", two);
        Assert.Contains("หน้า 2/2", two);
    }

    [Fact]
    public void CreditNote_ShouldPrintEveryRequiredItem()
    {
        var note = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);
        var s = note with
        {
            Lines = [note.Lines[0] with { OriginalAmountExVatSatang = 100_000, CorrectAmountExVatSatang = 50_000 }],
            Note = note.Note! with { ReasonKind = TaxNoteReason.GoodsReturned },
        };

        var html = Html(s);

        Assert.Contains("ใบลดหนี้", html);
        Assert.Contains("Credit Note", html);
        Assert.Contains("อ้างอิงใบกำกับภาษีเดิม", html);
        Assert.Contains("TAX-2026-000001", html);
        Assert.Contains("14/10/2569", html);
        Assert.Contains("มูลค่าตามใบกำกับภาษีเดิม", html);
        Assert.Contains("มูลค่าที่ถูกต้อง", html);
        Assert.Contains("ผลต่างรวม", html);
        Assert.Contains("ภาษีมูลค่าเพิ่ม 7% ของผลต่าง", html);
        Assert.Contains("จำนวนเงินที่ลดหนี้", html);
        Assert.Contains("<span class=\"cb\">✓</span> รับคืนสินค้า", html);
        Assert.Contains("<span class=\"cb\"></span> ลดราคาสินค้า/บริการ", html);
        Assert.Contains("Price correction", html);
        Assert.Contains("ผู้มีอำนาจลงนาม", html);
    }

    [Fact]
    public void Document_ShouldPrintTheCancelledWatermark_OnACancelledCopy()
    {
        Assert.Contains("ยกเลิก CANCELLED", Html(Receipt(1), TaxDocumentMarker.CancelledCopy));
        Assert.DoesNotContain("ยกเลิก CANCELLED", Html(Receipt(1)));
    }

    [Fact]
    public void Document_ShouldPrintTheReplacementReference_OnAReplacement()
    {
        var s = Receipt(1) with
        {
            Replacement = new TaxDocumentReplacement
            {
                ReplacesDocumentId = Guid.NewGuid(),
                OriginalDocumentNumber = "TAX-2026-000009",
                OriginalIssueDate = new DateOnly(2026, 10, 1),
                Reason = "Reissued",
            },
        };

        var html = Html(s);

        Assert.Contains("ใบแทนของเลขที่", html);
        Assert.Contains("TAX-2026-000009", html);
    }

    [Theory]
    [InlineData("00000", "สำนักงานใหญ่")]
    [InlineData("00012", "สาขาที่ 00012")]
    public void Party_ShouldPrintTheBranchInThai(string branch, string expected)
    {
        var s = Receipt(1) with { Seller = Receipt(1).Seller with { Branch = branch } };

        Assert.Contains(expected, Html(s));
    }
}
