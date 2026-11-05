using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentFormTests
{
    private static readonly TaxParty PlaceholderBuyer = new() { Name = "ลูกค้าทั่วไป" };

    private static TaxDocumentSnapshot Abbreviated() =>
        Samples.Receipt() with { Form = TaxDocumentForm.Abbreviated, Buyer = PlaceholderBuyer };

    [Fact]
    public void Serialize_ShouldKeepTheStoredBytesOfAFullSnapshot_WhenTheFormIsSetExplicitly()
    {
        var implicitFull = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());
        var explicitFull = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt() with { Form = TaxDocumentForm.Full });

        Assert.Equal(implicitFull, explicitFull);
        Assert.DoesNotContain("form", explicitFull, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ShouldWriteTheFormByNameAndReadItBack_WhenAbbreviated()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Abbreviated());

        Assert.Contains("\"form\":\"Abbreviated\"", json, StringComparison.Ordinal);
        var back = TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json));
        Assert.Equal(TaxDocumentForm.Abbreviated, back.Form);
    }

    [Fact]
    public void Validate_ShouldAccept_AnAbbreviatedReceiptWithAPlaceholderBuyer()
    {
        Assert.Empty(TaxDocumentSnapshotValidator.Validate(Abbreviated()));
    }

    [Fact]
    public void Validate_ShouldRefuse_AnAbbreviatedCreditNote()
    {
        var note = Samples.FullCancellationCreditNote() with { Form = TaxDocumentForm.Abbreviated };

        var errors = TaxDocumentSnapshotValidator.Validate(note);

        Assert.Contains(errors, e => e.Field == "form" && e.Code == TaxDocumentValidationCode.NotAllowed);
    }

    [Fact]
    public void Validate_ShouldRefuse_AnUndefinedForm()
    {
        var errors = TaxDocumentSnapshotValidator.Validate(Samples.Receipt() with { Form = (TaxDocumentForm)99 });

        Assert.Contains(errors, e => e.Field == "form" && e.Code == TaxDocumentValidationCode.Malformed);
    }

    [Fact]
    public async Task IssueAsync_ShouldPassTheFormToTheNumberSource()
    {
        var flow = new Flow();

        var result = await flow.IssueAsync(Flow.Request(snapshot: Abbreviated()));

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        Assert.Equal(TaxDocumentForm.Abbreviated, Assert.Single(flow.Numbers.Forms));
    }

    [Fact]
    public async Task IssueAsync_ShouldPassFullToTheNumberSource_WhenNoFormIsSet()
    {
        var flow = new Flow();

        await flow.IssueAsync(Flow.Request());

        Assert.Equal(TaxDocumentForm.Full, Assert.Single(flow.Numbers.Forms));
    }

    [Fact]
    public void RenderHtml_ShouldPrintTheExactTitleAndNoBuyerBlock_WhenAbbreviated()
    {
        var html = Html(Abbreviated());

        Assert.Contains("<h1>ใบกำกับภาษีอย่างย่อ</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderBuyer.Name, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Buyer", html, StringComparison.Ordinal);
        Assert.Contains(Samples.Receipt().Seller.Name, html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtml_ShouldKeepPrintingTheBuyer_WhenFull()
    {
        var html = Html(Samples.Receipt());

        Assert.Contains(Samples.Receipt().Buyer.Name, html, StringComparison.Ordinal);
    }

    private static string Html(TaxDocumentSnapshot snapshot) => System.Net.WebUtility.HtmlDecode(
        new TaxDocumentRenderer(new NullPdf()).RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original));

    private sealed class NullPdf : Themia.Pdf.IVersionedPdfRenderer
    {
        public Task<byte[]> RenderHtmlAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Themia.Pdf.PdfRenderResult> RenderHtmlWithVersionAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
