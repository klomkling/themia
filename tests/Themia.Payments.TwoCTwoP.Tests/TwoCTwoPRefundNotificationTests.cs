using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.DependencyInjection;
using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

/// <summary>
/// The asynchronous refund notification: <c>notifyResponse=&lt;JWS around JWE&gt;</c>, signed by 2C2P and encrypted to the merchant key. The notification is built here the way
/// 2C2P would send it (sealed with the merchant public key, signed with the provider private key).
/// </summary>
public sealed class TwoCTwoPRefundNotificationTests : IDisposable
{
    private const string Merchant = "JT01";

    private readonly RSA merchantKey = RSA.Create(2048);
    private readonly RSA providerKey = RSA.Create(2048);
    private readonly string providerCertificatePem;

    public TwoCTwoPRefundNotificationTests()
    {
        var request = new CertificateRequest("CN=2c2p-test", providerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        providerCertificatePem = certificate.ExportCertificatePem();
    }

    public void Dispose()
    {
        merchantKey.Dispose();
        providerKey.Dispose();
    }

    private TwoCTwoPRefundNotificationVerifier Verifier(Action<TwoCTwoPOptions>? configure = null)
    {
        var options = new TwoCTwoPOptions
        {
            MerchantId = Merchant,
            SecretKey = "s",
            MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem(),
            ProviderPublicCertificatePem = providerCertificatePem,
        };
        configure?.Invoke(options);
        return new TwoCTwoPRefundNotificationVerifier(Options.Create(options));
    }

    private static string Xml(string status = "RF", string respCode = "00", string invoiceNo = "order-1", string? merchant = Merchant, string? processType = "R", string timeStamp = "261030150000", string extra = "") =>
        "<PaymentProcessResponse><version>4.3</version>"
        + $"<timeStamp>{timeStamp}</timeStamp><respCode>{respCode}</respCode><respDesc>Success</respDesc>"
        + (processType is null ? "" : $"<processType>{processType}</processType>")
        + $"<invoiceNo>{invoiceNo}</invoiceNo><amount>25.00</amount><status>{status}</status><approvalCode>2508513089</approvalCode>"
        + "<referenceNo>3528643</referenceNo><refundReferenceNo>3598743</refundReferenceNo><transactionDateTime>20210126085854</transactionDateTime>"
        + "<maskedPan>411111XXXXXX1111</maskedPan><eci>05</eci><paymentScheme>VI</paymentScheme><processBy>VI</processBy>"
        + (merchant is null ? "" : $"<merchantID>{merchant}</merchantID>") + extra + "</PaymentProcessResponse>";

    private string Token(string xml) => JweJws.Seal(xml, merchantKey, providerKey);

    private static byte[] Body(string token) => Encoding.UTF8.GetBytes("notifyResponse=" + token);

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    [Theory]
    [InlineData("RF", PaymentEventType.RefundSucceeded, PaymentStatus.Succeeded)]
    [InlineData("RFF", PaymentEventType.RefundFailed, PaymentStatus.Failed)]
    [InlineData("RR", PaymentEventType.RefundFailed, PaymentStatus.Failed)]
    [InlineData("RR2", PaymentEventType.RefundFailed, PaymentStatus.Failed)]
    [InlineData("RP", PaymentEventType.Other, PaymentStatus.Pending)]
    [InlineData("SOMETHING-NEW", PaymentEventType.Other, PaymentStatus.Pending)]
    public void Verify_ShouldMapTheStatusLikeThePoll(string status, PaymentEventType type, PaymentStatus expected)
    {
        var result = Verifier().Verify(Body(Token(Xml(status))), NoHeaders);

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(type, result.Event!.Type);
        Assert.Equal(expected, result.Event.Status);
        Assert.Equal("order-1", result.Event.ChargeId);
        Assert.Equal("order-1", result.Event.ReferenceId);
        Assert.Null(result.Event.Amount);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.FromHours(7)), result.Event.OccurredAt);
    }

    [Fact]
    public void Verify_ShouldNeverReportSuccess_WhenTheRespCodeIsNotOk_WhateverTheStatus()
    {
        var result = Verifier().Verify(Body(Token(Xml("RF", respCode: "99"))), NoHeaders);

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.Other, result.Event!.Type);
        Assert.Equal(PaymentStatus.Pending, result.Event.Status);
    }

    [Fact]
    public void Verify_ShouldGiveTheSameEvent_WhenTheSameBodyIsReplayed()
    {
        var verifier = Verifier();
        var body = Body(Token(Xml("RF")));

        var first = verifier.Verify(body, NoHeaders);
        var second = verifier.Verify(body, NoHeaders);

        Assert.Equal(WebhookOutcome.Verified, second.Outcome);
        Assert.Equal(first.Event, second.Event);
    }

    [Fact]
    public void Verify_ShouldCarryTheReconciliationFieldsInRawJson_AndNothingAboutTheCard()
    {
        var xml = Xml("RF", extra: "<idempotencyID>refund-42</idempotencyID><userDefined1>refund-42</userDefined1>");

        var result = Verifier().Verify(Body(Token(xml)), NoHeaders);

        using var json = JsonDocument.Parse(result.Event!.RawJson);
        Assert.Equal("refund-42", json.RootElement.GetProperty("idempotencyID").GetString());
        Assert.Equal("refund-42", json.RootElement.GetProperty("userDefined1").GetString());
        Assert.Equal("3598743", json.RootElement.GetProperty("refundReferenceNo").GetString());
        Assert.Equal("3528643", json.RootElement.GetProperty("referenceNo").GetString());
        Assert.DoesNotContain("411111", result.Event.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("maskedPan", result.Event.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ShouldDecodeAFormBody_WithPercentEscapesPlusAndExtraFields()
    {
        var token = Token(Xml("RF"));
        var body = Encoding.UTF8.GetBytes("other=1&notifyResponse=" + Uri.EscapeDataString(token).Replace("%2E", ".") + "&more=%20x");

        Assert.Equal(WebhookOutcome.Verified, Verifier().Verify(body, NoHeaders).Outcome);
    }

    [Fact]
    public void Verify_ShouldReportSignatureMismatch_ForAnotherSigner_ATamperedToken_AndAWrongRecipient()
    {
        using var stranger = RSA.Create(2048);
        var verifier = Verifier();
        var good = Token(Xml("RF"));
        var parts = good.Split('.');
        var flipped = parts[2][..^2] + (parts[2][^2] == 'A' ? "B" : "A") + parts[2][^1];

        var wrongSigner = JweJws.Seal(Xml("RF"), merchantKey, stranger);
        var wrongRecipient = JweJws.Seal(Xml("RF"), stranger, providerKey);
        var tampered = string.Join('.', parts[0], parts[1], flipped);

        Assert.All(new[] { wrongSigner, wrongRecipient, tampered }, token =>
            Assert.Equal(WebhookOutcome.SignatureMismatch, verifier.Verify(Body(token), NoHeaders).Outcome));
    }

    [Fact]
    public void Verify_ShouldRefuseAnEnvelopeThatNamesOtherAlgorithmsOrKeys()
    {
        var verifier = Verifier();
        var parts = Token(Xml("RF")).Split('.');
        var header = Convert.ToBase64String(Encoding.ASCII.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('=');

        var token = string.Join('.', header, parts[1], parts[2]);

        Assert.Equal(WebhookOutcome.SignatureMismatch, verifier.Verify(Body(token), NoHeaders).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notifyResponse=")]
    [InlineData("notifyResponse")]
    [InlineData("payload=abc")]
    [InlineData("notifyResponse=a&notifyResponse=b")]
    [InlineData("{\"payload\":\"abc\"}")]
    public void Verify_ShouldReportMalformed_ForABodyWithoutExactlyOneNotifyResponse(string body) =>
        Assert.Equal(WebhookOutcome.Malformed, Verifier().Verify(Encoding.UTF8.GetBytes(body), NoHeaders).Outcome);

    [Fact]
    public void Verify_ShouldReportMalformed_ForInvalidUtf8_AndAnOversizedBody_WithoutOpeningAnything()
    {
        var verifier = Verifier();

        Assert.Equal(WebhookOutcome.Malformed, verifier.Verify([0xFF, 0xFE, 0x6E], NoHeaders).Outcome);
        Assert.Equal(WebhookOutcome.Malformed, verifier.Verify(Encoding.UTF8.GetBytes("notifyResponse=" + new string('a', 64 * 1024)), NoHeaders).Outcome);
    }

    [Theory]
    [InlineData("merchant")]
    [InlineData("process")]
    [InlineData("invoice")]
    [InlineData("time")]
    [InlineData("xxe")]
    [InlineData("notxml")]
    public void Verify_ShouldReportMalformed_ForAnAuthenticButWrongOrUnreadableNotification(string flaw)
    {
        var xml = flaw switch
        {
            "merchant" => Xml(merchant: "OTHER"),
            "process" => Xml(processType: "RS"),
            "invoice" => Xml(invoiceNo: ""),
            "time" => Xml(timeStamp: "not-a-time"),
            "xxe" => """<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><PaymentProcessResponse><respCode>00</respCode><invoiceNo>&e;</invoiceNo></PaymentProcessResponse>""",
            _ => "not xml at all",
        };

        Assert.Equal(WebhookOutcome.Malformed, Verifier().Verify(Body(Token(xml)), NoHeaders).Outcome);
    }

    [Fact]
    public void Verify_ShouldAccept_ANotificationWithNoMerchantIdOrProcessType()
    {
        var result = Verifier().Verify(Body(Token(Xml("RF", merchant: null, processType: null))), NoHeaders);

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Verify_ShouldThrow_WhenTheRefundKeysAreMissingOrInvalid_AndTryAgainOnTheNextCall()
    {
        var options = new TwoCTwoPOptions { MerchantId = Merchant, SecretKey = "s" };
        var verifier = new TwoCTwoPRefundNotificationVerifier(Options.Create(options));
        var body = Body(Token(Xml("RF")));

        Assert.Throws<InvalidOperationException>(() => verifier.Verify(body, NoHeaders));

        options.MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem();
        options.ProviderPublicCertificatePem = providerCertificatePem;
        Assert.Equal(WebhookOutcome.Verified, verifier.Verify(body, NoHeaders).Outcome);
    }

    [Fact]
    public void Verify_ShouldNotLoadTheKeysForAJunkBody()
    {
        var verifier = new TwoCTwoPRefundNotificationVerifier(Options.Create(new TwoCTwoPOptions { MerchantId = Merchant, SecretKey = "s" }));

        Assert.Equal(WebhookOutcome.Malformed, verifier.Verify("junk"u8, NoHeaders).Outcome);
    }

    [Fact]
    public void RefundRequest_ShouldCarryNotifyUrl_OnlyWhenOneIsConfigured()
    {
        var plain = TwoCTwoPRefundXml.BuildRequest("JT01", "order-1", 25m, "r-1", DateTimeOffset.UtcNow);
        var notified = TwoCTwoPRefundXml.BuildRequest("JT01", "order-1", 25m, "r-1", DateTimeOffset.UtcNow, "https://example.test/2c2p/refund");

        Assert.DoesNotContain("notifyURL", plain, StringComparison.Ordinal);
        Assert.Contains("<notifyURL>https://example.test/2c2p/refund</notifyURL>", notified, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.test/hook")]
    [InlineData("/relative")]
    [InlineData("https://user:pw@example.test/hook")]
    [InlineData("https://example.test/hook#frag")]
    [InlineData("not a url")]
    public void ValidateOnStart_ShouldRejectANotifyUrlThatIsNotAPlainHttpsUrl(string url)
    {
        var provider = Register(o => o.RefundNotifyUrl = url);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value);
        Assert.Contains("RefundNotifyUrl", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOnStart_ShouldRejectANotifyUrlWithoutRefundKeys_AndAcceptOneWithThem()
    {
        var withoutKeys = Register(o => o.RefundNotifyUrl = "https://example.test/hook");
        var withKeys = Register(o =>
        {
            o.RefundNotifyUrl = "https://example.test/hook";
            o.MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem();
            o.ProviderPublicCertificatePem = providerCertificatePem;
        });

        var ex = Assert.Throws<OptionsValidationException>(() => withoutKeys.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value);
        Assert.Contains("RefundNotifyUrl", string.Join(" ", ex.Failures), StringComparison.Ordinal);
        Assert.Equal("https://example.test/hook", withKeys.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value.RefundNotifyUrl);
    }

    [Fact]
    public void Registration_ShouldKeepThePaymentWebhookVerifierAsTheInterface_AndExposeTheRefundVerifierAsItself()
    {
        var provider = Register(o => { });

        Assert.IsType<TwoCTwoPWebhookVerifier>(provider.GetRequiredService<Themia.Payments.IPaymentWebhookVerifier>());
        Assert.NotNull(provider.GetRequiredService<TwoCTwoPRefundNotificationVerifier>());
    }

    private ServiceProvider Register(Action<TwoCTwoPOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsTwoCTwoP(o =>
        {
            o.MerchantId = Merchant;
            o.SecretKey = "s";
            configure(o);
        });
        return services.BuildServiceProvider();
    }
}
