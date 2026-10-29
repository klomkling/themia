using System.Net;
using System.Text.Json;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template versions "6" (A4 portrait) and "7" (A5 landscape) are versions "4" and "5" with a payment line that also has Card, Mobile banking and Wallet.
/// Versions "4" and "5" stay as issued (<see cref="TemplateVersion4GoldenTests"/>), so a receipt paid by card cannot be issued under them.
/// </summary>
public sealed class TemplateVersion6Tests
{
    private static string Html(TaxPaymentMethod method, string version) =>
        WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(
            TemplateVersion4Tests.Receipt(1) with { Payment = new TaxPayment { Method = method, Date = new DateOnly(2026, 10, 14) } },
            version, TaxDocumentMarker.Original, null));

    [Theory]
    [InlineData("6")]
    [InlineData("7")]
    public void PaymentLine_ShouldTickOnlyTheMethodUsed(string version)
    {
        Assert.Contains("✓</span> บัตร", Html(TaxPaymentMethod.Card, version));
        Assert.Contains("✓</span> แอปธนาคาร", Html(TaxPaymentMethod.MobileBanking, version));
        Assert.Contains("✓</span> อีวอลเล็ต", Html(TaxPaymentMethod.Wallet, version));
        Assert.Contains("✓</span> เงินสด", Html(TaxPaymentMethod.Cash, version));
        Assert.Contains("✓</span> โอนเงิน", Html(TaxPaymentMethod.Transfer, version));

        var card = Html(TaxPaymentMethod.Card, version);
        Assert.Equal(1, card.Split("✓</span>").Length - 1);
    }

    [Fact]
    public void Versions6And7_ShouldBeSupported_WithTheSamePaperAsTheirPredecessors()
    {
        Assert.True(TaxDocumentTemplates.IsSupported("6"));
        Assert.True(TaxDocumentTemplates.IsSupported("7"));
        Assert.Equal(TaxDocumentTemplates.PageSizeOf("4"), TaxDocumentTemplates.PageSizeOf("6"));
        Assert.Equal(TaxDocumentTemplates.PageSizeOf("5"), TaxDocumentTemplates.PageSizeOf("7"));
        Assert.Equal(TaxDocumentTemplates.EtModeOf("4"), TaxDocumentTemplates.EtModeOf("6"));
        Assert.False(TaxDocumentTemplates.IsSupported("8"));
    }

    [Theory]
    [InlineData(TaxPaymentMethod.Card)]
    [InlineData(TaxPaymentMethod.MobileBanking)]
    [InlineData(TaxPaymentMethod.Wallet)]
    public async Task Issue_ShouldRefuseTheNewMethods_UnderVersionsBefore6(TaxPaymentMethod method)
    {
        var snapshot = Samples.Receipt() with { Payment = new TaxPayment { Method = method } };
        foreach (var version in new[] { "1", "3", "4", "5" })
        {
            var flow = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = version });

            var result = await flow.IssueAsync(Flow.Request(snapshot: snapshot));

            Assert.Equal(TaxDocumentIssueOutcome.ValidationFailed, result.Outcome);
            Assert.Contains(result.Errors, e => e.Field == "payment.method" && e.Code == TaxDocumentValidationCode.NotAllowed);
        }
    }

    [Theory]
    [InlineData("6")]
    [InlineData("7")]
    public async Task Issue_ShouldAcceptTheNewMethods_UnderVersions6And7(string version)
    {
        var snapshot = Samples.Receipt() with { Payment = new TaxPayment { Method = TaxPaymentMethod.Card } };
        var flow = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = version });

        var result = await flow.IssueAsync(Flow.Request(snapshot: snapshot));

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    public async Task Issue_ShouldKeepAcceptingCashAndTransfer_UnderOlderVersions(string version)
    {
        var flow = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = version });

        foreach (var method in new[] { TaxPaymentMethod.Cash, TaxPaymentMethod.Transfer })
        {
            var snapshot = Samples.Receipt() with { Payment = new TaxPayment { Method = method } };
            var result = await flow.IssueAsync(Flow.Request(source: "pay_" + method, snapshot: snapshot));
            Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        }
    }

    [Theory]
    [InlineData(TaxPaymentMethod.Cash, "Cash")]
    [InlineData(TaxPaymentMethod.Transfer, "Transfer")]
    [InlineData(TaxPaymentMethod.Card, "Card")]
    [InlineData(TaxPaymentMethod.MobileBanking, "MobileBanking")]
    [InlineData(TaxPaymentMethod.Wallet, "Wallet")]
    public void Method_ShouldSerializeByName(TaxPaymentMethod method, string name)
    {
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(method));
        Assert.Equal(method, JsonSerializer.Deserialize<TaxPaymentMethod>($"\"{name}\""));
    }
}
