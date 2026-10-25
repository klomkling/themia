using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template version "4" (A5 landscape receipt/tax invoice and credit note) is what every stored document issued under it is re-rendered with, for years.
/// This pins the complete HTML of a one-line receipt, a full page, a receipt that continues on a second page, a copy and a credit note. A failure means:
/// ship template version "5" and leave "4" untouched. It never means: update the hash.
/// </summary>
public sealed class TemplateVersion4GoldenTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { "receipt-1-line", "78d57d88307e5e0968278108fa5a38cb434194117686121c0b83fed2c5e43861" },
        { "receipt-full-page", "7cde5b42f333d22601d8105a3cd254cdfc3001a777348fd2791e45290837fcaa" },
        { "receipt-2-pages", "dc83156d4dc7881bde41110ee2831e5a0dfe92a2f0eb55c03dc915db750f2afa" },
        { "receipt-copy", "b28f0c521abef3d2c252fb22542d08346968087bab69cbe3768437b5b98ececc" },
        { "credit-note", "cdeebeb29db615d88f224fe5a1484451d18dcbcefc0e869aed8b38c850f14958" },
    };

    internal static (TaxDocumentSnapshot Snapshot, TaxDocumentMarker Marker) Fixture(string name) => name switch
    {
        "receipt-1-line" => (TemplateVersion4Tests.Receipt(1), TaxDocumentMarker.Original),
        "receipt-full-page" => (TemplateVersion4Tests.Receipt(A5Layout.SinglePageRows(TaxDocumentKind.TaxInvoiceReceipt)), TaxDocumentMarker.Original),
        "receipt-2-pages" => (TemplateVersion4Tests.Receipt(8), TaxDocumentMarker.Original),
        "receipt-copy" => (TemplateVersion4Tests.Receipt(2), TaxDocumentMarker.Copy),
        "credit-note" => (CreditNote(), TaxDocumentMarker.Original),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

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
    public void Html_ShouldMatchTheGolden_BecauseVersion4MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, marker) = Fixture(name);
        var html = new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, "4", marker);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void PrintOptions_ShouldMatchTheGolden_BecauseTheyAreHalfOfWhatVersion4Prints()
    {
        var o = TaxDocumentRenderer.A5LandscapePrintOptions;
        var text = $"{o.PaperFormat}|{o.Landscape}|{o.PrintBackground}|{o.Tagged}|{o.MarginTop}|{o.MarginBottom}|{o.MarginLeft}|{o.MarginRight}";

        Assert.Equal("A5|True|True|False|0mm|0mm|0mm|0mm", text);
    }
}
