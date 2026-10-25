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
    internal sealed class NoPdf : IVersionedPdfRenderer
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
        { "single-copy1", "f3c15c2bddffab846515e29abfd5243d6a32e11a8a7540aa29b00ba6dfdb8e1f" },
        { "single-copy2", "142329ebd8733c0427537f5ddd936abda0bc631cee53555027202909cc23f796" },
        { "everything-copy1", "40a4df81e9fee8e3f306a556879d304843a00b2db6b625d5221a01988b7962c0" },
        { "single-replacement-copy", "37cde4a2419ad9dc2306b770629fd63bbe1aee63c929e98df261643e10a66057" },
        { "single-cancelled", "af6c555cab693ff47ef838c9f4cd501a9f9ad76357c3faf0907f90fa619e5acd" },
        { "single-signed-png", "89f399f1fd376fe51e38d7ee7978e42412480e03ed347b66dcee70e76700e273" },
        { "everything-signed-jpeg", "29c59a3a504d804f21c3bbd249f5c69a2516250724893645cd944fd14074c93f" },
    };

    internal static (WithholdingCertificateSnapshot Snapshot, WithholdingCopy Copy, WithholdingRenderMode Mode, WithholdingSignatureImage? Signature) Fixture(string name) => name switch
    {
        "single-copy1" => (Samples.Valid(), WithholdingCopy.Copy1, WithholdingRenderMode.Issued, null),
        "single-copy2" => (Samples.Valid(), WithholdingCopy.Copy2, WithholdingRenderMode.Issued, null),
        "everything-copy1" => (Everything(), WithholdingCopy.Copy1, WithholdingRenderMode.Issued, null),
        "single-replacement-copy" => (Samples.Valid(), WithholdingCopy.Copy1, WithholdingRenderMode.ReplacementCopy, null),
        "single-cancelled" => (Samples.Valid(), WithholdingCopy.Copy2, WithholdingRenderMode.Cancelled, null),
        "single-signed-png" => (Samples.Valid(), WithholdingCopy.Copy1, WithholdingRenderMode.Issued, new WithholdingSignatureImage(SignatureFixtures.Png)),
        "everything-signed-jpeg" => (Everything(), WithholdingCopy.Copy2, WithholdingRenderMode.Issued, new WithholdingSignatureImage(SignatureFixtures.Jpeg)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseVersion1MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, copy, mode, signature) = Fixture(name);
        var html = new WithholdingCertificateRenderer(new NoPdf()).RenderHtml(snapshot, "1", copy, mode, signature);

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
