using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.Extensions.Options;

using Themia.Payments.Beam.Tests;
using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

/// <summary>
/// Refunds through the Payment Maintenance API: XML in a JWE (RSA-OAEP + A256GCM) inside a JWS (PS256). The key pairs are generated here;
/// nothing was verified against the 2C2P sandbox.
/// </summary>
public sealed class TwoCTwoPRefundTests : IDisposable
{
    private const string Secret = "test-secret";
    private const string Merchant = "JT01";

    private readonly RSA merchantKey = RSA.Create(2048);
    private readonly RSA providerKey = RSA.Create(2048);
    private readonly string providerCertificatePem;

    public TwoCTwoPRefundTests()
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

    private TwoCTwoPPaymentGateway Gateway(StubHandler handler, Action<TwoCTwoPOptions>? configure = null)
    {
        var options = new TwoCTwoPOptions
        {
            MerchantId = Merchant,
            SecretKey = Secret,
            MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem(),
            ProviderPublicCertificatePem = providerCertificatePem,
        };
        configure?.Invoke(options);
        var client = new HttpClient(handler) { BaseAddress = TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Sandbox) };
        return new TwoCTwoPPaymentGateway(new StubFactory(client), Options.Create(options), new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions())));
    }

    private static string PaidInquiry(string invoiceNo = "order-1", decimal amount = 1000.00m, string currency = "THB", string respCode = "0000") =>
        JsonSerializer.Serialize(new
        {
            payload = JwtHs256.Encode(new Dictionary<string, object?>
            {
                ["invoiceNo"] = invoiceNo, ["amount"] = amount, ["currencyCode"] = currency,
                ["respCode"] = respCode, ["respDesc"] = "Success", ["transactionDateTime"] = "20260922100000",
            }, Secret),
        });

    private string RefundResponse(string status = "RP", string respCode = "00", string invoiceNo = "order-1", string merchant = Merchant) =>
        JweJws.Seal(
            $"<PaymentProcessResponse><version>4.3</version><respCode>{respCode}</respCode><respDesc>Success</respDesc><processType>R</processType>"
            + $"<invoiceNo>{invoiceNo}</invoiceNo><merchantID>{merchant}</merchantID><status>{status}</status></PaymentProcessResponse>",
            merchantKey, providerKey);

    // What 2C2P would decrypt: the request body opened with 2C2P's private key, checked against the merchant's public key.
    private XElement SentXml(StubHandler handler)
    {
        Assert.True(JweJws.TryOpen(handler.Bodies[^1], merchantKey, providerKey, out var xml));
        return XElement.Parse(xml);
    }

    private static RefundRequest Refund(Money? amount, string? key = "r-1", string reference = "order-1") =>
        new(new ChargeRef(null, reference), amount, "cancelled", key);

    [Fact]
    public async Task Refund_ShouldSendAFullRefundOfTheChargesAmount_ToTheActionEndpoint()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse("RP"));

        var creation = await Gateway(handler).RefundAsync(Refund(null));

        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.Null(creation.RefundId);
        Assert.Equal("https://demo2.2c2p.com/PaymentAction/2.0/action", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("application/*+json", handler.Requests[1].Content!.Headers.ContentType!.MediaType);
        var xml = SentXml(handler);
        Assert.Equal("PaymentProcessRequest", xml.Name.LocalName);
        Assert.Equal("R", xml.Element("processType")!.Value);
        Assert.Equal("order-1", xml.Element("invoiceNo")!.Value);
        Assert.Equal(Merchant, xml.Element("merchantID")!.Value);
        Assert.Equal("1000.00", xml.Element("actionAmount")!.Value);
        Assert.Equal("r-1", xml.Element("idempotencyID")!.Value);
        Assert.Matches(@"^\d{12}$", xml.Element("timeStamp")!.Value);
    }

    [Fact]
    public async Task Refund_ShouldSendAPartialAmount_AsATwoPlaceDecimal()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse());

        await Gateway(handler).RefundAsync(Refund(Money.Thb(12345)));

        Assert.Equal("123.45", SentXml(handler).Element("actionAmount")!.Value);
    }

    [Theory]
    [InlineData("RF", PaymentStatus.Succeeded)]
    [InlineData("RP", PaymentStatus.Pending)]
    [InlineData("RFF", PaymentStatus.Failed)]
    [InlineData("RR1", PaymentStatus.Failed)]
    [InlineData("SOMETHING_NEW", PaymentStatus.Pending)]
    public async Task Refund_ShouldMapTheRefundStatus_AndNeverReadAnUnknownOneAsSucceeded(string status, PaymentStatus expected)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse(status));

        Assert.Equal(expected, (await Gateway(handler).RefundAsync(Refund(null))).Status);
    }

    [Fact]
    public async Task Refund_ShouldGenerateAnIdempotencyId_WhenTheCallerGivesNone()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse());

        await Gateway(handler).RefundAsync(Refund(null, key: null));

        Assert.Matches("^[0-9a-f]{32}$", SentXml(handler).Element("idempotencyID")!.Value);
    }

    [Fact]
    public async Task Refund_ShouldEscapeTheInvoiceNumberInTheXml()
    {
        const string hostile = "a</invoiceNo><x>";
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry(hostile)).Enqueue(HttpStatusCode.OK, RefundResponse(invoiceNo: "a&lt;/invoiceNo&gt;&lt;x&gt;"));

        await Gateway(handler).RefundAsync(Refund(null, reference: hostile));

        var xml = SentXml(handler);
        Assert.Equal(hostile, xml.Element("invoiceNo")!.Value);
        Assert.Null(xml.Element("x"));
    }

    [Fact]
    public async Task Refund_ShouldRefuseWithoutKeys_AndSendNothing()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler, o => o.MerchantPrivateKeyPem = null).RefundAsync(Refund(null)));

        Assert.Equal("refund_not_supported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refund_ShouldRefuseUnusableKeys_WithoutEchoingThem()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry());

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler, o => o.MerchantPrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nsecret-material\n-----END PRIVATE KEY-----").RefundAsync(Refund(null)));

        Assert.Equal("refund_keys_invalid", ex.ProviderCode);
        Assert.DoesNotContain("secret-material", ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "refund_amount_invalid")]
    [InlineData(100001L, "refund_amount_exceeds_charge")]
    public async Task Refund_ShouldRefuseAnAmountOutsideTheCharge_BeforeSendingARefund(long minorUnits, string code)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry());

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(Money.Thb(minorUnits))));

        Assert.Equal(code, ex.ProviderCode);
        Assert.All(handler.Requests, r => Assert.DoesNotContain("PaymentAction", r.RequestUri!.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refund_ShouldRefuseAnotherCurrency()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry());

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(Money.From(100, "USD"))));

        Assert.Equal("refund_currency_mismatch", ex.ProviderCode);
    }

    [Fact]
    public async Task Refund_ShouldRefuseAChargeThatIsNotPaid()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry(respCode: "0001"));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("charge_not_refundable", ex.ProviderCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Refund_ShouldSendOnce_AndReportAnUnknownOutcome_OnAServerError(HttpStatusCode status)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(status, "oops");

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Refund_ShouldSendOnce_AndReportAnUnknownOutcome_OnATransportFailure()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).EnqueueThrow(() => new HttpRequestException("reset"));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Refund_ShouldReportAnUnknownOutcome_WhenTheCallerCancelsAfterTheRequestIsSent()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).EnqueueThrow(() =>
        {
            cts.Cancel();
            return new TaskCanceledException("cancelled", null, cts.Token);
        });

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null), cts.Token));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
    }

    [Fact]
    public async Task Refund_ShouldRefuseAResponseThatEchoesAnotherIdempotencyId()
    {
        var other = JweJws.Seal(
            "<PaymentProcessResponse><respCode>00</respCode><invoiceNo>order-1</invoiceNo><status>RF</status><idempotencyID>someone-else</idempotencyID></PaymentProcessResponse>",
            merchantKey, providerKey);
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, other);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null, key: "r-1")));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
    }

    [Fact]
    public async Task Refund_ShouldNotTrustAResponseSignedByAnotherKey()
    {
        using var stranger = RSA.Create(2048);
        var forged = JweJws.Seal("<PaymentProcessResponse><respCode>00</respCode><invoiceNo>order-1</invoiceNo><status>RF</status></PaymentProcessResponse>", merchantKey, stranger);
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, forged);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
    }

    [Theory]
    [InlineData("other-order", Merchant)]
    [InlineData("order-1", "JT99")]
    public async Task Refund_ShouldNotAcceptASignedResponseForAnotherTransaction(string invoiceNo, string merchant)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse("RF", invoiceNo: invoiceNo, merchant: merchant));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
    }

    [Fact]
    public async Task Refund_ShouldReportAnUnknownOutcome_ForARespCodeItDoesNotKnow()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse(respCode: "9999"));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).RefundAsync(Refund(null)));

        Assert.Equal("refund_outcome_unknown", ex.ProviderCode);
        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
        Assert.Equal(FailureKind.Unknown, ex.Kind);
    }

    [Fact]
    public void Refund_ShouldNotParseAResponseThatDeclaresADtd()
    {
        const string xxe = "<!DOCTYPE r [<!ENTITY e \"x\">]><PaymentProcessResponse><respCode>00</respCode></PaymentProcessResponse>";

        Assert.Null(TwoCTwoPRefundXml.ParseResponse(xxe));
    }

    [Fact]
    public void ValidateOnStart_ShouldRejectHalfConfiguredRefundKeys_AndExpiredCertificates()
    {
        var half = new TwoCTwoPOptions { MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem() };
        Assert.False(TwoCTwoPRefundKeys.IsConfigured(half));

        var request = new CertificateRequest("CN=old", providerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        var options = new TwoCTwoPOptions { MerchantPrivateKeyPem = merchantKey.ExportPkcs8PrivateKeyPem(), ProviderPublicCertificatePem = expired.ExportCertificatePem() };

        Assert.False(TwoCTwoPRefundKeys.TryCreate(options, TimeProvider.System, out _, out var error));
        Assert.Contains("validity", error, StringComparison.Ordinal);
    }

    // ---- Refund status (processType RS) ----

    private string RefundStatusResponse(string refunds, string respCode = "00", string invoiceNo = "order-1", string merchant = Merchant) =>
        JweJws.Seal(
            $"<PaymentProcessResponse><version>4.3</version><respCode>{respCode}</respCode><respDesc>Success</respDesc><processType>RS</processType>"
            + $"<invoiceNo>{invoiceNo}</invoiceNo><merchantID>{merchant}</merchantID><status>S</status><refundList>{refunds}</refundList></PaymentProcessResponse>",
            merchantKey, providerKey);

    private static string RefundRow(string status = "RF", string amount = "25.00", string reference = "3598743", string userDefined1 = "r-1") =>
        $"<refund amount=\"{amount}\" status=\"{status}\" referenceNo=\"{reference}\" dateTime=\"20260225155130\" userDefined1=\"{userDefined1}\" userDefined2=\"\" acquirerResponseCode=\"00\"/>";

    [Fact]
    public async Task Refund_ShouldSendTheIdempotencyKeyAsUserDefined1_SoTheRefundCanBeMatchedLater()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse());

        await Gateway(handler).RefundAsync(Refund(null, key: "refund-42"));

        Assert.Equal("refund-42", SentXml(handler).Element("userDefined1")!.Value);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task Refund_ShouldSendTheKeyAsUserDefined1_OnlyWhenItFits(int length, bool sent)
    {
        var key = new string('k', length);
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundResponse());

        await Gateway(handler).RefundAsync(Refund(null, key: key));

        var xml = SentXml(handler);
        Assert.Equal(key, xml.Element("idempotencyID")!.Value);
        Assert.Equal(sent ? key : null, xml.Element("userDefined1")?.Value);
    }

    [Fact]
    public async Task GetRefunds_ShouldAskForTheRefundStatusOfTheInvoice()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(RefundRow()));

        await Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1"));

        var xml = SentXml(handler);
        Assert.Equal("RS", xml.Element("processType")!.Value);
        Assert.Equal("order-1", xml.Element("invoiceNo")!.Value);
        Assert.Equal(Merchant, xml.Element("merchantID")!.Value);
        Assert.Equal("https://demo2.2c2p.com/PaymentAction/2.0/action", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetRefunds_ShouldReturnEachRefund_WithTheKeyTheCallerSent()
    {
        var rows = RefundRow("RF", "25.00", "3598743", "r-1") + RefundRow("RP", "10.50", "3598744", "r-2");
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(rows));

        var refunds = await Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1"));

        Assert.Equal(2, refunds.Count);
        Assert.Equal(new RefundRecord("3598743", Money.Thb(2500), PaymentStatus.Succeeded, new DateTimeOffset(2026, 2, 25, 15, 51, 30, TimeSpan.FromHours(7)), "r-1"), refunds[0]);
        Assert.Equal("r-2", refunds[1].IdempotencyKey);
        Assert.Equal(Money.Thb(1050), refunds[1].Amount);
        Assert.Equal(PaymentStatus.Pending, refunds[1].Status);
    }

    [Theory]
    [InlineData("RF", PaymentStatus.Succeeded)]
    [InlineData("RP", PaymentStatus.Pending)]
    [InlineData("RFF", PaymentStatus.Failed)]
    [InlineData("RR2", PaymentStatus.Failed)]
    [InlineData("SOMETHING_NEW", PaymentStatus.Pending)]
    public async Task GetRefunds_ShouldMapTheStatusLikeARefund_AndNeverReadAnUnknownOneAsSucceeded(string status, PaymentStatus expected)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(RefundRow(status)));

        Assert.Equal(expected, Assert.Single(await Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1"))).Status);
    }

    [Fact]
    public async Task GetRefunds_ShouldReturnNothing_ForAChargeWithNoRefunds()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(string.Empty));

        Assert.Empty(await Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1")));
    }

    [Fact]
    public async Task GetRefunds_ShouldLeaveTheKeyNull_WhenTheRefundCameFromElsewhere()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(RefundRow(userDefined1: string.Empty)));

        Assert.Null(Assert.Single(await Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1"))).IdempotencyKey);
    }

    [Fact]
    public async Task GetRefunds_ShouldRefuseAResponseForAnotherInvoice()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(RefundRow(), invoiceNo: "order-2"));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task GetRefunds_ShouldRefuseAMalformedRefundRow_RatherThanSkipIt()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(RefundRow(amount: "not-a-number")));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task GetRefunds_ShouldReportA2C2PErrorWithItsCode()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.OK, RefundStatusResponse(string.Empty, respCode: "42"));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1")));

        Assert.Equal("42", ex.ProviderCode);
    }

    [Fact]
    public async Task GetRefunds_ShouldTreatATransportFailureAsTransient_NotAsAnUnknownRefundOutcome()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PaidInquiry()).Enqueue(HttpStatusCode.ServiceUnavailable, "down");

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => Gateway(handler).GetRefundsAsync(new ChargeRef(null, "order-1")));

        Assert.Equal(FailureKind.Transient, ex.Kind);
        Assert.NotEqual("refund_outcome_unknown", ex.ProviderCode);
    }

    [Fact]
    public async Task GetRefunds_ShouldRefuseWithoutKeys_AndSendNothing()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<PaymentApiException>(
            () => Gateway(handler, o => o.MerchantPrivateKeyPem = null).GetRefundsAsync(new ChargeRef(null, "order-1")));

        Assert.Equal("refund_not_supported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
