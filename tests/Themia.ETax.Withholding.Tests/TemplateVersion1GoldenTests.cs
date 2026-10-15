using System.Security.Cryptography;
using System.Text;
using Themia.Pdf;
using Xunit;

namespace Themia.ETax.Withholding.Tests;

/// <summary>
/// Template version "1" is what every stored certificate is re-rendered with, for years. What it prints depends on the .hbs, the fonts AND the
/// C# that builds the model (labels, ordering, formatting) and the print options, so this pins the complete HTML for each marking.
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

    private static WithholdingCertificateSnapshot Everything()
    {
        var s = Samples.Valid();
        var rows = Enum.GetValues<WithholdingFormRow>().Select((row, i) => s.Rows[0] with
        {
            IncomeTypeCode = $"fixture-{i + 1}",
            FormRow = row,
            Section = "40(2)",
            LabelTh = $"รายการที่ {i + 1} Café Łódź",
            Description = row is WithholdingFormRow.Other or WithholdingFormRow.Section3Tredecim ? $"ค่าจ้างทำของ {i + 1}" : null,
            DatePaid = new DateOnly(2026, 10, 1 + i),
            AmountPaidSatang = 1_000_000 + i,
            RateBasisPoints = null,
            TaxWithheldSatang = 30_000 + i,
        }).ToArray();
        return s with
        {
            BookNumber = "12",
            FilingSequence = 17,
            FilingForm = WithholdingFilingForm.Pnd53,
            PayerLegacyTaxId = "0123456789",
            PayeeLegacyTaxId = "9876543210",
            Rows = rows,
            Totals = new WithholdingTotals { PaidSatang = rows.Sum(r => r.AmountPaidSatang), WithheldSatang = rows.Sum(r => r.TaxWithheldSatang) },
            Funds = new WithholdingFunds { GovernmentPensionSatang = 150_000, SocialSecuritySatang = 75_000, ProvidentFundSatang = 300_000 },
            Condition = WithholdingCondition.Other,
            ConditionOther = "ออกให้ตามสัญญา",
        };
    }

    public static TheoryData<string, string> Documents => new()
    {
        { "single-copy1", "266d95adde4a89eb990c6b66e5f6426d05b296b0296d2701ab94b0b89221ba11" },
        { "single-copy2", "6af766ec627f626c8fbb05392dba8373319f3ff402cb4c19f63009addef4552d" },
        { "everything-copy1", "c5ac29e50e5c6acf8ebb754931074728c876119d79aa1d3fe2c4ec71b7092181" },
        { "single-replacement-copy", "7167760fee410da89f811d7b1133942fa6ba2bdc71fbbf42689116800cdd2291" },
        { "single-cancelled", "aff17d9bf95f6dbb8be32d61e3050fa695f2c980b9f7571461fcbf5c22f9777c" },
    };

    private static (WithholdingCertificateSnapshot Snapshot, WithholdingCopy Copy, WithholdingRenderMode Mode) Fixture(string name) => name switch
    {
        "single-copy1" => (Samples.Valid(), WithholdingCopy.Copy1, WithholdingRenderMode.Issued),
        "single-copy2" => (Samples.Valid(), WithholdingCopy.Copy2, WithholdingRenderMode.Issued),
        "everything-copy1" => (Everything(), WithholdingCopy.Copy1, WithholdingRenderMode.Issued),
        "single-replacement-copy" => (Samples.Valid(), WithholdingCopy.Copy1, WithholdingRenderMode.ReplacementCopy),
        "single-cancelled" => (Samples.Valid(), WithholdingCopy.Copy2, WithholdingRenderMode.Cancelled),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseVersion1MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, copy, mode) = Fixture(name);
        var html = new WithholdingCertificateRenderer(new NoPdf()).RenderHtml(snapshot, WithholdingTemplates.CurrentVersion, copy, mode);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void PrintOptions_ShouldMatchTheGolden_BecauseTheyAreHalfOfWhatVersion1Prints()
    {
        var o = WithholdingCertificateRenderer.PrintOptions;
        var text = $"{o.PaperFormat}|{o.PrintBackground}|{o.Tagged}|{o.MarginTop}|{o.MarginBottom}|{o.MarginLeft}|{o.MarginRight}";

        Assert.Equal("A4|True|False|8mm|8mm|8mm|8mm", text);
    }
}
