using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentFormTests
{
    private static readonly TaxParty PlaceholderBuyer = new() { Name = "ลูกค้าทั่วไป" };

    private static TaxDocumentSnapshot Abbreviated() =>
        Samples.Receipt() with
        {
            Form = TaxDocumentForm.Abbreviated,
            Buyer = PlaceholderBuyer,
            Lines = [new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = 100_000, Quantity = 2.5m, Unit = "ชิ้น", UnitPriceSatang = 40_000 }],
            Totals = Samples.Receipt().Totals with { WithheldSatang = 0, NetReceivedSatang = 107_000 },
        };

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
    public void Validate_ShouldRefuse_AnAbbreviatedReceiptWithWithholding()
    {
        var withheld = Abbreviated() with { Totals = Samples.Receipt().Totals };

        var errors = TaxDocumentSnapshotValidator.Validate(withheld);

        Assert.Contains(errors, e => e.Field == "totals.withheld_satang" && e.Code == TaxDocumentValidationCode.NotAllowed);
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
    public void RenderHtml_ShouldPrintTheQuantityAndUnit_WhenAbbreviated()
    {
        Assert.Contains("<td class=\"num\">2.5 ชิ้น</td>", Html(Abbreviated()), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHtml_ShouldLeaveTheQuantityBlankAndNotRefuse_WhenALineHasNoQuantity()
    {
        var noQuantity = Abbreviated() with { Lines = [new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = 100_000 }] };

        Assert.Empty(TaxDocumentSnapshotValidator.Validate(noQuantity));
        Assert.Contains("<td class=\"num\"></td>", Html(noQuantity), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaxDocumentMarker.Original, "2d1f5d31987e28cac94a9268a06da5df03dfe1059de19954d8b074ed9781da35")]
    [InlineData(TaxDocumentMarker.CancelledCopy, "2bda78dd1b9f1c4159701d3664a67d8435b5d0fdd0686a877ebf505be96e594d")]
    public void Html_ShouldMatchTheGolden_BecauseTheAbbreviatedTemplateMustNeverChange(TaxDocumentMarker marker, string expectedSha256)
    {
        // Every abbreviated document renders with this one template whatever version it records, so editing it changes every stored copy:
        // ship a new form or template key instead.
        var html = new TaxDocumentRenderer(new NullPdf()).RenderHtml(Abbreviated(), TaxDocumentTemplates.CurrentVersion, marker);

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        Assert.Equal(expectedSha256, hash);
    }

    [Fact]
    public async Task RenderAsync_ShouldUseTheSingleSheetPrintOptions_WhenAbbreviatedUnderAPagedVersion()
    {
        var plain = new CapturingPdf();
        var paged = new CapturingPdf();

        await new TaxDocumentRenderer(plain).RenderAsync(Samples.Receipt(), TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original);
        await new TaxDocumentRenderer(paged).RenderAsync(Abbreviated(), "4", TaxDocumentMarker.Original);

        Assert.Equal(plain.Options, paged.Options);
    }

    [Fact]
    public async Task IssueAsync_ShouldCancelTheAbbreviatedDocument_WhenAFullReplacementIsIssued()
    {
        var flow = new Flow();
        var first = await flow.IssueAsync(Flow.Request(snapshot: Abbreviated()));
        var old = await flow.GetAsync(first.DocumentId!.Value);
        var upgrade = new IssueTaxDocumentRequest
        {
            Key = new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, "pay_1", 2),
            SellerId = Flow.Seller,
            Snapshot = Samples.Receipt() with
            {
                Replacement = new TaxDocumentReplacement
                {
                    ReplacesDocumentId = old.Id,
                    OriginalDocumentNumber = old.DocumentNumber,
                    OriginalIssueDate = old.IssueDate,
                    Reason = "Full tax invoice requested",
                },
            },
        };

        var result = await flow.IssueAsync(upgrade);

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        Assert.Equal(TaxDocumentState.Cancelled, (await flow.GetAsync(old.Id)).State);
        Assert.Equal(TaxDocumentForm.Full, flow.Numbers.Forms[^1]);
    }

    [Fact]
    public void RenderHtml_ShouldKeepPrintingTheBuyer_WhenFull()
    {
        var html = Html(Samples.Receipt());

        Assert.Contains(Samples.Receipt().Buyer.Name, html, StringComparison.Ordinal);
    }

    private static string Html(TaxDocumentSnapshot snapshot) => System.Net.WebUtility.HtmlDecode(
        new TaxDocumentRenderer(new NullPdf()).RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original));

    private sealed class CapturingPdf : Themia.Pdf.IVersionedPdfRenderer
    {
        public Themia.Pdf.PdfRenderOptions? Options { get; private set; }

        public Task<byte[]> RenderHtmlAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Themia.Pdf.PdfRenderResult> RenderHtmlWithVersionAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default)
        {
            Options = options;
            return Task.FromResult(new Themia.Pdf.PdfRenderResult(System.Text.Encoding.Latin1.GetBytes("%PDF-1.4\n<< /CreationDate (D:20260101120000+00'00') /ModDate (D:20260101120001+00'00') >>\n%%EOF"), "test"));
        }
    }

    private sealed class NullPdf : Themia.Pdf.IVersionedPdfRenderer
    {
        public Task<byte[]> RenderHtmlAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Themia.Pdf.PdfRenderResult> RenderHtmlWithVersionAsync(string html, Themia.Pdf.PdfRenderOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
