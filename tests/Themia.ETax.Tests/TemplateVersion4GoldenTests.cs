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
        { "4", "receipt-1-line", "2d8b90a3d99cb8e0f56e02fea04bb3a0302f0f2d5395d79e141fe788018bc7fe" },
        { "4", "receipt-full-page", "f657dcf12ae452319d7e2a9a7a9df668e58af8b23c5d38c3697aad4739c1c6f0" },
        { "4", "receipt-2-pages", "56399de670c1ae9f1f33bec7d4e3db3d674e177d5491bcf5e2df5661192520f0" },
        { "4", "receipt-copy", "b4fc7d928a2874a86ab922e62cd372f531d39723422cc8420c1766258de1c4a0" },
        { "4", "credit-note", "344fbe98438b3ae0b1f83555c0ac0c2ee3e51c1d0176dc8648f4f150f6bebce8" },
        { "4", "debit-note", "35a431a80a9c329c8f6bf16c293cb31738bde6e4915963b35633420237192a65" },
        { "5", "receipt-1-line", "bc83b5df2e8269493158c0358ff94b18083aeb33dcf97bc46ab890ba62552838" },
        { "5", "receipt-full-page", "71dc38184c913a30beb17a417b732d676d8b478f018e04aadd4480a6182953dd" },
        { "5", "receipt-2-pages", "f67beed27311e422388f5477c75e3157068c6d08e07cead89353668b083dead8" },
        { "5", "receipt-copy", "685e66db3c13b0fa736a2a0249dddc816408604a23cb040b47b7f6b8597568fd" },
        { "5", "credit-note", "6b245cf7bdf72bd90ba35454470751d479b44587f026ec790d513874bb1c15e3" },
        { "5", "debit-note", "5527944fb1243db86e2d942d5d293c974d0301ac147acfe729b99fabd531796a" },
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
