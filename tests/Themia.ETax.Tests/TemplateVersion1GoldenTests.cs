using System.Security.Cryptography;
using System.Text;
using Themia.Pdf;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template version "1" is what every stored document is re-rendered with, for years. What it prints depends on the
/// .hbs, the fonts AND the C# that builds the model (formatting, labels, titles, markers) and the print options, so this
/// pins the complete HTML for each kind and marker plus the print options.
/// A failure here means: ship template version "2" (and leave "1" untouched). It never means: update the hash.
/// </summary>
public sealed class TemplateVersion1GoldenTests
{
    private sealed class NoPdf : IVersionedPdfRenderer
    {
        public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PdfRenderResult> RenderHtmlWithVersionAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static TaxDocumentSnapshot ReceiptWithoutWithholding() => Samples.Receipt() with
    {
        Totals = Samples.Receipt().Totals with { WithheldSatang = 0, NetReceivedSatang = 107_000 },
    };

    private static TaxDocumentSnapshot ReplacementReceipt() => Samples.Receipt() with
    {
        Buyer = new TaxParty { Name = "Café Łódź Ltd.", TaxId = Samples.BuyerTaxId, Branch = "00012", Address = "5 Ő utca\nBudapest" },
        Lines =
        [
            new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = 60_000 },
            new TaxDocumentLine { Description = "Add-on: รายการเพิ่มเติม", AmountExVatSatang = 40_000 },
        ],
        Replacement = new TaxDocumentReplacement
        {
            ReplacesDocumentId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            OriginalDocumentNumber = "TAX-2026-000000",
            OriginalIssueDate = new DateOnly(2026, 10, 13),
            Reason = "Wrong buyer address",
        },
    };

    public static TheoryData<string, string> Documents => new()
    {
        { "receipt-withheld-original", "a2f29598c6eb733a60339c186cb32138d3748b6a05f4ec668e9b7c851e319a6d" },
        { "receipt-copy", "1e9d43057ce6b1e203a54188611839fe344b48c3cf0942d9378a545f42ad6521" },
        { "credit-full-copy", "5abbe145b91b5f27fee765f68051166a4256869c65b17df72b8ba2e2b16df8e6" },
        { "credit-adjustment-copy", "56a9e24bdabc1cd749455d9f109b578fe4c9d3c55e1dabf4aca76a363d8f2bd5" },
        { "debit-adjustment-copy", "b97962330e2072aeeef6900740beb0b9d9d1c8d37c2cb5792751022e860563f8" },
        { "replacement-original", "95dfc7485f64b6ecc0c82aab2be2f9b50400733c1d13a12f8b1bc1fbf144c8ad" },
        { "receipt-cancelled-copy", "87ab7e8024aa5d36862a55512666ba20faaffbef8702efbd237770793a94ae50" },
    };

    private static (TaxDocumentSnapshot Snapshot, TaxDocumentMarker Marker) Fixture(string name) => name switch
    {
        "receipt-withheld-original" => (Samples.Receipt(), TaxDocumentMarker.Original),
        "receipt-copy" => (ReceiptWithoutWithholding(), TaxDocumentMarker.Copy),
        "credit-full-copy" => (Samples.FullCancellationCreditNote(), TaxDocumentMarker.Copy),
        "credit-adjustment-copy" => (Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 200_000, 150_000), TaxDocumentMarker.Copy),
        "debit-adjustment-copy" => (Samples.AdjustmentNote(TaxDocumentKind.DebitNote, 150_000, 200_000), TaxDocumentMarker.Copy),
        "replacement-original" => (ReplacementReceipt(), TaxDocumentMarker.Original),
        "receipt-cancelled-copy" => (Samples.Receipt(), TaxDocumentMarker.CancelledCopy),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseVersion1MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, marker) = Fixture(name);
        var html = new TaxDocumentRenderer(new NoPdf())
            .RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, marker);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void PrintOptions_ShouldMatchVersion1()
    {
        var o = TaxDocumentRenderer.PrintOptions;

        Assert.Equal(PdfPaperFormat.A4, o.PaperFormat);
        Assert.True(o.PrintBackground);
        Assert.False(o.Tagged);
        Assert.Equal("15mm", o.MarginTop);
        Assert.Equal("15mm", o.MarginBottom);
        Assert.Equal("15mm", o.MarginLeft);
        Assert.Equal("15mm", o.MarginRight);
    }
}
