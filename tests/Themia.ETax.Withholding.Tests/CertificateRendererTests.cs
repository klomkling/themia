using System.Text;
using System.Text.RegularExpressions;
using Themia.Pdf;
using Xunit;

namespace Themia.ETax.Withholding.Tests;

public sealed partial class CertificateRendererTests
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

    private WithholdingCertificateRenderer Sut() => new(_pdf);

    private async Task<string> HtmlOf(
        WithholdingCertificateSnapshot? snapshot = null, WithholdingCopy copy = WithholdingCopy.Copy1, WithholdingRenderMode mode = WithholdingRenderMode.Issued)
    {
        await Sut().RenderAsync(snapshot ?? Samples.Valid(), WithholdingTemplates.CurrentVersion, copy, mode);
        return Decoded(_pdf.Html!);
    }

    // Handlebars writes every printed value as HTML entities (Thai included); the browser reads them back, so the tests do too.
    private static string Decoded(string html) => System.Net.WebUtility.HtmlDecode(html);

    // The table lines, each as the HTML of its <tr>.
    private static string[] Lines(string html) =>
        html.Split("<tr class=\"line\">", StringSplitOptions.None).Skip(1).Select(l => l[..l.IndexOf("</tr>", StringComparison.Ordinal)]).ToArray();

    private static string LineOf(string html, string labelStart) => Lines(html).Single(l => l.Contains(">" + labelStart, StringComparison.Ordinal));

    [Fact]
    public async Task RenderAsync_ShouldPrintTheOfficialTitleAndBothCopyLinesExactly()
    {
        var html = await HtmlOf();

        Assert.Contains("หนังสือรับรองการหักภาษี ณ ที่จ่าย", html);
        Assert.Contains("ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร", html);
        Assert.Contains("ฉบับที่ 1 (สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)", html);
        Assert.Contains("ฉบับที่ 2 (สำหรับผู้ถูกหักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)", html);
        Assert.Contains("<title>WHT-2569-000123</title>", html);
    }

    [Theory]
    [InlineData(WithholdingCopy.Copy1, "copy-1", "<div class=\"on\">ฉบับที่ 1", "<div>ฉบับที่ 2")]
    [InlineData(WithholdingCopy.Copy2, "copy-2", "<div>ฉบับที่ 1", "<div class=\"on\">ฉบับที่ 2")]
    public async Task RenderAsync_ShouldMarkTheCopyItIs_AndStillPrintBothLines(WithholdingCopy copy, string key, string first, string second)
    {
        var html = await HtmlOf(copy: copy);

        Assert.Contains($"data-copy=\"{key}\"", html);
        Assert.Contains(first, html);
        Assert.Contains(second, html);
    }

    [Fact]
    public async Task RenderAsync_ShouldWriteReplacementCopyAtTheTop_OnlyForAReplacementCopy()
    {
        Assert.DoesNotContain("ใบแทน", await HtmlOf());
        Assert.DoesNotContain("ใบแทน", await HtmlOf(mode: WithholdingRenderMode.Cancelled));

        var replacement = await HtmlOf(mode: WithholdingRenderMode.ReplacementCopy);

        Assert.Contains("<div class=\"replacement\"><span>ใบแทน</span></div>", replacement);
        Assert.Contains("WHT-2569-000123", replacement);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheCancelledWatermark_OnlyWhenCancelled()
    {
        Assert.DoesNotContain("class=\"cancelled-watermark\"", await HtmlOf());
        Assert.DoesNotContain("class=\"cancelled-watermark\"", await HtmlOf(mode: WithholdingRenderMode.ReplacementCopy));
        Assert.Contains("class=\"cancelled-watermark\"", await HtmlOf(mode: WithholdingRenderMode.Cancelled));
    }

    [Fact]
    public async Task RenderAsync_ShouldPutEachIncomeOnTheLineOfItsFormRow_AndLeaveTheOtherLinesBlank()
    {
        var snapshot = Samples.Valid() with
        {
            Rows = [Samples.Valid().Rows[0] with { AmountPaidSatang = 123_456_789, RateBasisPoints = null, TaxWithheldSatang = 3_703_704 }],
            Totals = new WithholdingTotals { PaidSatang = 123_456_789, WithheldSatang = 3_703_704 },
        };

        var html = await HtmlOf(snapshot);

        var fees = LineOf(html, "2. ค่าธรรมเนียม");
        Assert.Contains("14/10/2569", fees);
        Assert.Contains("<td class=\"baht\">1,234,567</td><td class=\"satang\">89</td>", fees);
        Assert.Contains("<td class=\"baht\">37,037</td><td class=\"satang\">04</td>", fees);
        Assert.Equal(18, Lines(html).Length);
        Assert.Single(Lines(html), l => l.Contains("14/10/2569", StringComparison.Ordinal));
        Assert.DoesNotContain("14/10/2569", LineOf(html, "1. เงินเดือน"));
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheSixLinesAndTheDividendSubItemsInTheOrderOfTheForm()
    {
        var html = await HtmlOf();
        var labels = Lines(html).Select(l => Regex.Match(l, @"class=""label[^>]*>(.*?)(<span|</td>)").Groups[1].Value).ToArray();

        Assert.StartsWith("1. ", labels[0], StringComparison.Ordinal);
        Assert.StartsWith("2. ", labels[1], StringComparison.Ordinal);
        Assert.StartsWith("3. ", labels[2], StringComparison.Ordinal);
        Assert.StartsWith("4. (ก)", labels[3], StringComparison.Ordinal);
        Assert.StartsWith("(ข) เงินปันผล", labels[4], StringComparison.Ordinal);
        Assert.Contains(labels, l => l.StartsWith("(1.1) อัตราร้อยละ 30", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.StartsWith("(1.4) อัตราอื่น ๆ (ระบุ)", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.StartsWith("(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)", StringComparison.Ordinal));
        Assert.StartsWith("5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส", labels[^2], StringComparison.Ordinal);
        Assert.StartsWith("6. อื่น ๆ (ระบุ)", labels[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheDescriptionOnTheDottedLineOfASpecifyRow_AndTheLabelWhenThereIsNone()
    {
        var s = Samples.Valid();
        var withText = s.Rows[0] with { FormRow = WithholdingFormRow.Other, Description = "ค่าจ้างทำของ" };
        var withoutText = s.Rows[0] with { FormRow = WithholdingFormRow.Section3Tredecim, Description = null, LabelTh = "ค่าโฆษณา" };
        var html = await HtmlOf(s with
        {
            Rows = [withText, withoutText],
            Totals = new WithholdingTotals { PaidSatang = 2_000_000, WithheldSatang = 60_000 },
        });

        Assert.Contains("<span class=\"fill\">ค่าจ้างทำของ</span>", LineOf(html, "6. อื่น"));
        Assert.Contains("<span class=\"fill\">ค่าโฆษณา</span>", LineOf(html, "5. การจ่ายเงินได้"));
        Assert.Contains("<span class=\"fill empty\"></span>", LineOf(html, "(1.4)"));
    }

    [Fact]
    public async Task RenderAsync_ShouldTickExactlyTheFilingFormOfTheCertificate()
    {
        var html = await HtmlOf(Samples.Valid() with { FilingForm = WithholdingFilingForm.Pnd53, FilingSequence = 7 });

        Assert.Contains("<span class=\"cb on\"></span>(7) <b>ภ.ง.ด.53</b>", html);
        Assert.Single(Regex.Matches(html, @"<span class=""cb on""></span>\(\d\) <b>ภ"));
        foreach (var label in new[] { "ภ.ง.ด.1ก", "ภ.ง.ด.1ก พิเศษ", "ภ.ง.ด.2", "ภ.ง.ด.3", "ภ.ง.ด.2ก", "ภ.ง.ด.3ก", "ภ.ง.ด.53" })
            Assert.Contains($"<b>{label}</b>", html);
        Assert.Contains("<span class=\"box\">7</span>", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheTotalsAndTheTaxInThaiWords()
    {
        var html = await HtmlOf();

        Assert.Contains("<td class=\"baht\">10,000</td><td class=\"satang\">00</td><td class=\"baht\">300</td><td class=\"satang\">00</td>", html);
        Assert.Contains("<span class=\"val\">สามร้อยบาทถ้วน</span>", html);
    }

    private static WithholdingCertificateSnapshot WithTaxOf101Baht()
    {
        var s = Samples.Valid();
        return s with
        {
            Rows = [s.Rows[0] with { RateBasisPoints = null, TaxWithheldSatang = 10_100 }],
            Totals = new WithholdingTotals { PaidSatang = s.Totals.PaidSatang, WithheldSatang = 10_100 },
        };
    }

    [Fact]
    public async Task RenderAsync_ShouldReadAUnitsDigitOfOneAsNeung_BecauseTheConventionIsFixedByTheTemplate()
    {
        Assert.Contains("<span class=\"val\">หนึ่งร้อยหนึ่งบาทถ้วน</span>", await HtmlOf(WithTaxOf101Baht()));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateRenderer(null!));
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheFundsLine_WithBlanksWhenThereAreNone()
    {
        var blank = await HtmlOf();
        Assert.Contains("<span class=\"v\"></span> บาท", blank);

        var html = await HtmlOf(Samples.Valid() with
        {
            Funds = new WithholdingFunds { GovernmentPensionSatang = 150_000, SocialSecuritySatang = 75_000, ProvidentFundSatang = 300_000 },
        });
        Assert.Contains("กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน <span class=\"v\">1,500.00</span> บาท", html);
        Assert.Contains("กองทุนประกันสังคม <span class=\"v\">750.00</span> บาท", html);
        Assert.Contains("กองทุนสำรองเลี้ยงชีพ <span class=\"v\">3,000.00</span> บาท", html);
    }

    [Theory]
    [InlineData(WithholdingCondition.WithheldAtSource, "(1) หัก ณ ที่จ่าย")]
    [InlineData(WithholdingCondition.PaidOnBehalfForever, "(2) ออกให้ตลอดไป")]
    [InlineData(WithholdingCondition.PaidOnBehalfOnce, "(3) ออกให้ครั้งเดียว")]
    public async Task RenderAsync_ShouldTickExactlyTheConditionOfThePayer(WithholdingCondition condition, string label)
    {
        var html = await HtmlOf(Samples.Valid() with { Condition = condition });
        var conditions = html[html.IndexOf("class=\"cond\"", StringComparison.Ordinal)..html.IndexOf("class=\"bottom\"", StringComparison.Ordinal)];

        Assert.Single(Regex.Matches(conditions, "cb on"));
        Assert.Contains($"<span class=\"cb on\"></span>{label}", conditions);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheOtherConditionText()
    {
        var html = await HtmlOf(Samples.Valid() with { Condition = WithholdingCondition.Other, ConditionOther = "ออกให้ตามสัญญา" });

        Assert.Contains("<span class=\"cb on\"></span>(4) อื่น ๆ (ระบุ) <span class=\"fill\">ออกให้ตามสัญญา</span>", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheTaxIdsInTheirBoxes_AndTheKindNextToTheName()
    {
        var snapshot = Samples.Valid() with { PayerLegacyTaxId = "0123456789" };
        var html = await HtmlOf(snapshot);

        var payer = html[html.IndexOf("data-party=\"payer\"", StringComparison.Ordinal)..html.IndexOf("data-party=\"payee\"", StringComparison.Ordinal)];
        var digits = Regex.Matches(payer, @"<span class=""cell"">(\d?)</span>").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(23, digits.Length);
        Assert.Equal(Samples.ValidPayerTaxId, string.Concat(digits.Take(13)));
        Assert.Equal("0123456789", string.Concat(digits.Skip(13)));
        Assert.Contains("<b>บริษัท</b>", payer);
        Assert.Contains("<span class=\"name\">บริษัท ตัวอย่าง จำกัด</span>", payer);
        Assert.Contains("<span class=\"addr\">1 ถนนสุขุมวิท กรุงเทพฯ</span>", payer);

        var payee = html[html.IndexOf("data-party=\"payee\"", StringComparison.Ordinal)..];
        Assert.Contains("<b>บุคคล</b>", payee);
        var empty = Regex.Matches(payee, @"<span class=""cell""></span>").Count;
        Assert.Equal(10, empty);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintTheBookAndNumberAndTheIssueDateInTheBuddhistEra()
    {
        var html = await HtmlOf(Samples.Valid() with { BookNumber = "12" });

        Assert.Contains("เล่มที่ <b>12</b>", html);
        Assert.Contains("เลขที่ <b>WHT-2569-000123</b>", html);
        Assert.Contains("<span class=\"d\">15</span> / <span class=\"d\">10</span> / <span class=\"d\">2569</span>", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldLeaveTheBookNumberEmpty_UnlessTheAdopterSuppliesOne()
    {
        var html = await HtmlOf(Samples.Valid() with { BookNumber = null });

        Assert.Contains("เล่มที่ <b></b>", html);
        Assert.Contains("เลขที่ <b>WHT-2569-000123</b>", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldNotPrintASignature_AndKeepTheStampOptional()
    {
        var html = await HtmlOf();

        Assert.DoesNotContain("<img", html);
        Assert.Contains("ประทับตรา<br>นิติบุคคล<br>(ถ้ามี)", html);
        Assert.Contains("ผู้จ่ายเงิน", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldEscapeHtmlInPrintedValues()
    {
        var s = Samples.Valid();
        await HtmlOf(s with { Payee = s.Payee with { Name = "<script>x</script> & Co" } });
        var html = _pdf.Html!;

        Assert.DoesNotContain("<script>x", html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt; &amp; Co", html);
    }

    [Fact]
    public async Task RenderAsync_ShouldReturnThePdfWithItsDatesPinnedToTheIssueDate_AndTheRendererVersion()
    {
        var render = await Sut().RenderAsync(Samples.Valid(), "1", WithholdingCopy.Copy1, WithholdingRenderMode.Issued);

        var text = Encoding.Latin1.GetString(render.Pdf);
        Assert.Contains("/CreationDate (D:20261015000000+00'00')", text);
        Assert.Contains("/ModDate (D:20261015000000+00'00')", text);
        Assert.Equal($"{FakeRendererVersion}|n1", render.RendererVersion);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(render.Pdf)).ToLowerInvariant(), render.PdfSha256);
    }

    [Fact]
    public async Task RenderAsync_ShouldPrintOnA4WithoutATaggedStructure()
    {
        await HtmlOf();

        Assert.Equal(PdfPaperFormat.A4, _pdf.Options!.PaperFormat);
        Assert.False(_pdf.Options.Tagged);
        Assert.True(_pdf.Options.PrintBackground);
    }

    [Fact]
    public async Task RenderAsync_ShouldRenderTheSameBytesTwice()
    {
        var a = await Sut().RenderAsync(Samples.Valid(), "1", WithholdingCopy.Copy2, WithholdingRenderMode.Issued);
        var b = await Sut().RenderAsync(Samples.Valid(), "1", WithholdingCopy.Copy2, WithholdingRenderMode.Issued);

        Assert.Equal(a.PdfSha256, b.PdfSha256);
    }

    [Fact]
    public async Task RenderAsync_ShouldRejectAnUnknownTemplateVersionCopyOrMode()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Sut().RenderAsync(Samples.Valid(), "9", WithholdingCopy.Copy1, WithholdingRenderMode.Issued));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sut().RenderAsync(Samples.Valid(), "1", (WithholdingCopy)9, WithholdingRenderMode.Issued));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sut().RenderAsync(Samples.Valid(), "1", WithholdingCopy.Copy1, (WithholdingRenderMode)9));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sut().RenderAsync(null!, "1", WithholdingCopy.Copy1, WithholdingRenderMode.Issued));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateRenderer(null!));
    }

    [Fact]
    public void Templates_ShouldSupportOnlyVersion1()
    {
        Assert.True(WithholdingTemplates.IsSupported("1"));
        Assert.False(WithholdingTemplates.IsSupported("2"));
        Assert.False(WithholdingTemplates.IsSupported(null));
        Assert.Equal("1", WithholdingTemplates.CurrentVersion);
    }

    // ---- stored certificates ---------------------------------------------------------------------------------------

    private static WithholdingCertificateRecord Stored(WithholdingCertificateState state = WithholdingCertificateState.Issued, string template = "1")
    {
        var snapshot = Samples.Valid();
        var json = WithholdingSnapshotSerializer.Serialize(snapshot);
        return new WithholdingCertificateRecord
        {
            Id = Guid.NewGuid(),
            SourceEventId = "pay_1",
            Revision = 1,
            PayerId = "payer-1",
            DocumentNumber = snapshot.DocumentNumber,
            IssueDate = snapshot.IssueDate,
            SnapshotJson = json,
            SnapshotSchemaVersion = snapshot.SchemaVersion,
            SnapshotSha256 = WithholdingSnapshotSerializer.ComputeHash(json),
            TemplateVersion = template,
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    [Fact]
    public async Task RenderStoredAsync_ShouldRenderTheStoredSnapshotWithItsTemplateVersion()
    {
        await Sut().RenderStoredAsync(Stored(), WithholdingCopy.Copy2);

        Assert.Contains("data-copy=\"copy-2\"", Decoded(_pdf.Html!));
        Assert.Contains("<title>WHT-2569-000123</title>", Decoded(_pdf.Html!));
        Assert.DoesNotContain("class=\"cancelled-watermark\"", Decoded(_pdf.Html!));
    }

    [Fact]
    public async Task RenderStoredAsync_ShouldPrintACancelledCertificateAsCancelled_WhateverModeIsAsked()
    {
        await Sut().RenderStoredAsync(Stored(WithholdingCertificateState.Cancelled), WithholdingCopy.Copy1, WithholdingRenderMode.ReplacementCopy);

        Assert.Contains("class=\"cancelled-watermark\"", Decoded(_pdf.Html!));
        Assert.DoesNotContain("ใบแทน", Decoded(_pdf.Html!));
    }

    [Fact]
    public async Task RenderStoredAsync_ShouldPrintAReplacementCopy_ForAnIssuedCertificate()
    {
        await Sut().RenderStoredAsync(Stored(), WithholdingCopy.Copy1, WithholdingRenderMode.ReplacementCopy);

        Assert.Contains("<span>ใบแทน</span>", Decoded(_pdf.Html!));
    }

    [Fact]
    public async Task RenderStoredAsync_ShouldRefuseATamperedSnapshot_WithoutRendering()
    {
        var record = Stored() with { SnapshotJson = Stored().SnapshotJson.Replace("WHT-2569-000123", "WHT-2569-000999", StringComparison.Ordinal) };

        var ex = await Assert.ThrowsAsync<TaxDocumentSnapshotException>(() => Sut().RenderStoredAsync(record, WithholdingCopy.Copy1));

        Assert.Equal(TaxDocumentSnapshotFailure.HashMismatch, ex.Failure);
        Assert.Null(_pdf.Html);
    }

    [Fact]
    public async Task RenderStoredAsync_ShouldRefuseAnUnknownStoredTemplateVersion()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Sut().RenderStoredAsync(Stored(template: "7"), WithholdingCopy.Copy1));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sut().RenderStoredAsync(null!, WithholdingCopy.Copy1));
    }
}
