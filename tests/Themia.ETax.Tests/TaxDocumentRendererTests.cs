using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Themia.Pdf;
using Xunit;

namespace Themia.ETax.Tests;

public sealed partial class TaxDocumentRendererTests
{
    private const string FakeRendererVersion = "HeadlessChrome/1.2.3";

    private sealed class RecordingPdfRenderer : IVersionedPdfRenderer
    {
        public string? Html { get; private set; }
        public PdfRenderOptions? Options { get; private set; }

        public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("The renderer must ask for the version too.");

        public Task<PdfRenderResult> RenderHtmlWithVersionAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
        {
            Html = html;
            Options = options;
            var pdf = Encoding.Latin1.GetBytes(
                "%PDF-1.4\n<< /CreationDate (D:20260101120000+00'00') /ModDate (D:20260101120001+00'00') >>\n%%EOF");
            return Task.FromResult(new PdfRenderResult(pdf, FakeRendererVersion));
        }
    }

    private readonly RecordingPdfRenderer _pdf = new();

    private TaxDocumentRenderer Sut() => new(_pdf);

    private async Task<string> HtmlOf(TaxDocumentSnapshot snapshot, TaxDocumentMarker marker = TaxDocumentMarker.Original)
    {
        await Sut().RenderAsync(snapshot, TaxDocumentTemplates.CurrentVersion, marker);
        return _pdf.Html!;
    }

    [Theory]
    [InlineData(TaxDocumentMarker.Original, "original")]
    [InlineData(TaxDocumentMarker.Copy, "copy")]
    [InlineData(TaxDocumentMarker.CancelledCopy, "cancelled-copy")]
    public async Task RenderAsync_ShouldStampTheMarker_WhenRenderingEachMarker(TaxDocumentMarker marker, string expected)
    {
        var html = await HtmlOf(Samples.Receipt(), marker);

        Assert.Contains($"data-marker=\"{expected}\"", html);
        Assert.Equal(marker == TaxDocumentMarker.CancelledCopy, html.Contains("class=\"cancelled-watermark\""));
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheDocumentNumberAndTitleIt()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.Contains("<title>TAX-2026-000001</title>", html);
        Assert.Contains("TAX-2026-000001", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintMoneyAsBahtAndTheIssueDate()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.Contains("1,000.00", html);
        Assert.Contains("70.00", html);
        Assert.Contains("1,070.00", html);
        Assert.Contains("14/10/2569", html);
        Assert.DoesNotContain("2026", html.Replace("TAX-2026-000001", ""));
    }

    [Theory]
    [InlineData(2026, 1, 1, "01/01/2569")]
    [InlineData(2026, 12, 31, "31/12/2569")]
    [InlineData(2024, 2, 29, "29/02/2567")]
    [InlineData(2000, 2, 29, "29/02/2543")]
    public void Date_ShouldPrintTheBuddhistYear_WhenGivenAGregorianDate(int year, int month, int day, string expected) =>
        Assert.Equal(expected, TaxDocumentRenderModel.Date(new DateOnly(year, month, day)));

    [Fact]
    public async Task RenderAsync_ShouldPrintTheBuddhistYearOnTheReferencedDocumentDate()
    {
        var note = Samples.FullCancellationCreditNote();
        var html = await HtmlOf(note with { Note = note.Note! with { OriginalIssueDate = new DateOnly(2024, 2, 29) } });

        Assert.Contains("(29/02/2567)", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldShowTheWithheldRowOnlyWhenSomethingWasWithheld()
    {
        var withheld = await HtmlOf(Samples.Receipt());
        var none = await HtmlOf(Samples.Receipt() with
        {
            Totals = Samples.Receipt().Totals with { WithheldSatang = 0, NetReceivedSatang = 107_000 },
        });

        Assert.Contains("class=\"withheld\"", withheld);
        Assert.DoesNotContain("class=\"withheld\"", none);
    }

    [Fact]
    public async Task RenderAsync_ShouldEscapeHtmlInEveryUserField()
    {
        var snapshot = Samples.Receipt() with
        {
            Buyer = new TaxParty { Name = "<script>alert(1)</script>", Address = "a & b\n<b>c</b>" },
            Lines = [new TaxDocumentLine { Description = "<img src=x onerror=1>", AmountExVatSatang = 100_000 }],
        };

        var html = await HtmlOf(snapshot);

        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>c</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheReferencedDocument_WhenRenderingACreditNote()
    {
        var html = await HtmlOf(Samples.FullCancellationCreditNote());

        Assert.Contains("class=\"note\"", html);
        Assert.Contains("TAX-2026-000001", html);
        Assert.Contains("Refund", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintBothValues_WhenRenderingAnAdjustmentNote()
    {
        var html = await HtmlOf(Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 200_000, 150_000));

        Assert.Contains("2,000.00", html);
        Assert.Contains("1,500.00", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldOmitTheNoteAndReplacementBlocks_WhenNeitherApplies()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.DoesNotContain("class=\"note\"", html);
        Assert.DoesNotContain("class=\"replacement\"", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheReplacementBlock_WhenTheDocumentReplacesAnother()
    {
        var html = await HtmlOf(Samples.Receipt() with
        {
            Replacement = new TaxDocumentReplacement
            {
                ReplacesDocumentId = Guid.NewGuid(),
                OriginalDocumentNumber = "TAX-2026-000000",
                OriginalIssueDate = new DateOnly(2026, 10, 13),
                Reason = "Wrong address",
            },
        });

        Assert.Contains("class=\"replacement\"", html);
        Assert.Contains("TAX-2026-000000", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldRepeatTheTableHeaderOnEveryPage()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.Contains("display: table-header-group", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldReferenceNothingOutsideTheDocument()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotMatch(NonDataUrl(), html);
    }

    [Fact]
    public async Task RenderAsync_ShouldEmbedBothSarabunFaces()
    {
        var html = await HtmlOf(Samples.Receipt());

        Assert.Equal(2, Regex.Count(html, "@font-face"));
        Assert.Contains("font-family: 'Sarabun'", html);
        Assert.Contains("font-weight: 700", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldReturnNormalisedPdfWithItsHashAndVersion()
    {
        var render = await Sut().RenderAsync(Samples.Receipt(), TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original);

        var text = Encoding.Latin1.GetString(render.Pdf);
        Assert.Contains("/CreationDate (D:20261014000000+00'00')", text);
        Assert.Contains("/ModDate (D:20261014000000+00'00')", text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(render.Pdf)).ToLowerInvariant(), render.PdfSha256);
        Assert.Equal($"{FakeRendererVersion}|n1", render.RendererVersion);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintOnA4WithBackgrounds()
    {
        await Sut().RenderAsync(Samples.Receipt(), TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Copy);

        Assert.Equal(PdfPaperFormat.A4, _pdf.Options!.PaperFormat);
        Assert.True(_pdf.Options.PrintBackground);
    }

    [Fact]
    public async Task RenderAsync_ShouldRejectAnUnknownTemplateVersion()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Sut().RenderAsync(Samples.Receipt(), "999", TaxDocumentMarker.Original));

        Assert.Equal("templateVersion", ex.ParamName);
    }

    [Fact]
    public async Task RenderAsync_ShouldRejectAnUndefinedMarker() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Sut().RenderAsync(Samples.Receipt(), TaxDocumentTemplates.CurrentVersion, (TaxDocumentMarker)99));

    [Fact]
    public void Version1_ShouldBeFrozen_BecauseStoredDocumentsAreRenderedFromIt()
    {
        // Changing the template or a font changes every future copy of every document that pinned version "1".
        // If this fails, ship template version "2"; never update these hashes. The C# side of version 1 (formatting,
        // labels, print options) is pinned by TemplateVersion1GoldenTests.
        Assert.Equal(
            "3aa6f5309cb8060cb7c9244a82112dd95ad766693983182c9ec85f078d8a8bde",
            Sha256Hex(Encoding.UTF8.GetBytes(TaxDocumentTemplates.Get("1"))));
        Assert.Equal("226d4f368fbc0457990ddef2692679badfd2c1a4e89e5ac4d43c10ba7743b2f1", Sha256Hex(TaxDocumentFonts.Regular));
        Assert.Equal("d38308ca27d067b9a1b79006337c1f9a66c29aa016d96a9d88d3801da7fa83b9", Sha256Hex(TaxDocumentFonts.Bold));
    }

    [Fact]
    public void Fonts_ShouldShipWithTheOflNotice() =>
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", TaxDocumentFonts.LicenseText);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex(@"url\((?!\s*['""]?data:)")]
    private static partial Regex NonDataUrl();
}
