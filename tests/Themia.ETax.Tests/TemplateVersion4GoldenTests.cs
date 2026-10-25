using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template versions "4" (A4 portrait) and "5" (A5 landscape) are what every stored document issued under them is re-rendered with, for years. This pins the
/// complete HTML of a one-line receipt, a full page, a receipt that continues on a second page, a copy, a credit note and a debit note, for each. A failure means: ship
/// template version "6" and leave "4" and "5" untouched. It never means: update the hash.
/// </summary>
public sealed class TemplateVersion4GoldenTests
{
    public static TheoryData<string, string, string> Documents => new()
    {
        { "4", "receipt-1-line", "3fe601ec167b8331ec4ec61a50c41b2725874da1035e1eab61170f4eb73157a1" },
        { "4", "receipt-full-page", "97a00407f7863b15f6d753f1e33ac851d51baed124a37c4f00d6699b48d6645f" },
        { "4", "receipt-2-pages", "ee3d649685970c7330ecd8cde8f542a74538e01799eca22d0367386d211f754b" },
        { "4", "receipt-copy", "667915e8f5ad0a715718fce07e713c5fd1aa0c0c5ae371585b973c2eecb3bbc4" },
        { "4", "credit-note", "50ddc7ef4d354f09b08075ef276064e0a7137948b22a97cf146432af870aeb62" },
        { "4", "debit-note", "b4b5c4ba67c1102665eb449d0d1d22b8de9319a671afa2f7c9297f5c9f4ae9cd" },
        { "5", "receipt-1-line", "b023060d3049b0568711733dffa0dc9d97992ef0c7c0c13248ca69c44a4272c1" },
        { "5", "receipt-full-page", "6ed9a4e53bff4d4cc836e21f258a34886c5452a95edf06c86b9426a1da67076b" },
        { "5", "receipt-2-pages", "770c627158b7f5344166971273568825fc48f02ec242c4e05dc2226041742c55" },
        { "5", "receipt-copy", "bc8fa63382de2e15ff65e43c3db9fd083be77680656b5564f3cd650cd4037acb" },
        { "5", "credit-note", "c56979c3140b7645ffd4b1e99d7142178c71b76f49c897fb0b555a796b268d12" },
        { "5", "debit-note", "cba96a1b11ae8b08fba57c5e7e9a7c9f200975882cb0913e8951a2b784c14d01" },
    };

    private static TaxDocumentPageSize SizeOf(string version) => TaxDocumentTemplates.PageSizeOf(version)!.Value;

    internal static (TaxDocumentSnapshot Snapshot, TaxDocumentMarker Marker) Fixture(string version, string name)
    {
        var single = TaxDocumentPageLayout.SinglePageRows(SizeOf(version), TaxDocumentKind.TaxInvoiceReceipt);
        return name switch
        {
            "receipt-1-line" => (TemplateVersion4Tests.Receipt(1), TaxDocumentMarker.Original),
            "receipt-full-page" => (TemplateVersion4Tests.Receipt(single), TaxDocumentMarker.Original),
            "receipt-2-pages" => (TemplateVersion4Tests.Receipt(single + 2), TaxDocumentMarker.Original),
            "receipt-copy" => (TemplateVersion4Tests.Receipt(2), TaxDocumentMarker.Copy),
            "credit-note" => (CreditNote(), TaxDocumentMarker.Original),
            "debit-note" => (TemplateVersion4Tests.DebitNote(), TaxDocumentMarker.Original),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    private static TaxDocumentSnapshot CreditNote()
    {
        var note = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);
        return note with
        {
            Lines = [note.Lines[0] with { OriginalAmountExVatSatang = 100_000, CorrectAmountExVatSatang = 50_000 }],
            Note = note.Note! with { ReasonKind = TaxNoteReason.GoodsReturned },
        };
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseTheseVersionsMustNeverChange(string version, string name, string expectedSha256)
    {
        var (snapshot, marker) = Fixture(version, name);
        var html = new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, version, marker);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void PrintOptions_ShouldMatchTheGolden_BecauseTheyAreHalfOfWhatTheseVersionsPrint()
    {
        static string Text(Themia.Pdf.PdfRenderOptions o) =>
            $"{o.PaperFormat}|{o.Landscape}|{o.PrintBackground}|{o.Tagged}|{o.MarginTop}|{o.MarginBottom}|{o.MarginLeft}|{o.MarginRight}";

        Assert.Equal("A4|False|True|False|0mm|0mm|0mm|0mm", Text(TaxDocumentRenderer.A4PortraitPagedPrintOptions));
        Assert.Equal("A5|True|True|False|0mm|0mm|0mm|0mm", Text(TaxDocumentRenderer.A5LandscapePrintOptions));
    }
}
