using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments;
using Themia.Payments.Beam.Tests;
using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

public class TwoCTwoPGatewayTests
{
    private const string Secret = "test-secret";

    [Fact]
    public async Task Creating_a_charge_sends_a_jwt_payload_and_returns_the_hosted_url()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["webPaymentUrl"] = "https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc",
            ["paymentToken"] = "abc",
            ["respCode"] = "0000",
            ["respDesc"] = "Success",
        }));
        var gateway = BuildGateway(handler);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(100000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card, PaymentMethod.QrPromptPay],
        });

        Assert.Equal("/payment/4.3/paymentToken", handler.Requests[0].RequestUri!.AbsolutePath);
        var sent = PayloadOf(handler.Bodies[0]);
        Assert.Equal("order-1", sent.GetProperty("invoiceNo").GetString());
        Assert.Equal("1000.00", sent.GetProperty("amount").GetRawText());   // a number; a string would fail here
        Assert.Equal(["CC", "THQR"], sent.GetProperty("paymentChannel").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new Uri("https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc"),
            Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.Equal("order-1", creation.ChargeId);              // 2C2P mints no id before payment
    }

    [Fact]
    public async Task A_reference_longer_than_twenty_characters_is_refused_before_the_call()
    {
        var handler = new StubHandler();
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(1000),
            ReferenceId = new string('x', 21),
            AllowedMethods = [PaymentMethod.Card],
        }));

        Assert.Equal("reference_id_too_long", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_method_outside_the_configured_channels_is_refused_before_the_call()
    {
        var handler = new StubHandler();
        var gateway = BuildGateway(handler, o => o.PaymentChannels = [PaymentMethod.Card]);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(1000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card, PaymentMethod.QrPromptPay],
        }));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("method_not_supported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void SupportedMethods_is_all_four_channels_when_none_are_configured()
    {
        var gateway = BuildGateway(new StubHandler());

        Assert.Equal(
            [PaymentMethod.Card, PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking, PaymentMethod.Wallet],
            gateway.SupportedMethods);
    }

    [Fact]
    public void SupportedMethods_is_the_configured_channels_when_some_are_set()
    {
        var gateway = BuildGateway(new StubHandler(), o => o.PaymentChannels = [PaymentMethod.QrPromptPay]);

        Assert.Equal([PaymentMethod.QrPromptPay], gateway.SupportedMethods);
    }

    [Fact]
    public async Task Reading_a_charge_inquires_by_the_apps_own_reference()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success", ["transactionDateTime"] = "20260922100000",
        }));
        var gateway = BuildGateway(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

        Assert.Equal("/payment/4.3/paymentInquiry", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("order-1", PayloadOf(handler.Bodies[0]).GetProperty("invoiceNo").GetString());
        Assert.Equal(PaymentStatus.Succeeded, charge.Status);
        Assert.Equal(Money.Thb(100000), charge.Amount);          // 1000.00 THB back into minor units
        // 2C2P's transactionDateTime carries no offset; the default TransactionTimeOffset (+07:00) applies.
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(7)), charge.CompletedAt);
    }

    [Fact]
    public async Task Reading_a_charge_uses_the_configured_transaction_time_offset()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success", ["transactionDateTime"] = "20260922100000",
        }));
        var gateway = BuildGateway(handler, o => o.TransactionTimeOffset = TimeSpan.Zero);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero), charge.CompletedAt);
    }

    [Fact]
    public async Task An_absent_transactionDateTime_leaves_completedAt_null()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success",
        }));
        var gateway = BuildGateway(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

        Assert.Null(charge.CompletedAt);
    }

    [Fact]
    public async Task An_empty_transactionDateTime_leaves_completedAt_null()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success", ["transactionDateTime"] = "",
        }));
        var gateway = BuildGateway(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

        Assert.Null(charge.CompletedAt);
    }

    [Fact]
    public async Task An_unparsable_transactionDateTime_is_a_malformed_response()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success", ["transactionDateTime"] = "2026-09-22T10:00:00",
        }));
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(
            () => gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Theory]
    [InlineData("0001", PaymentStatus.Pending, FailureReason.Unknown)]
    [InlineData("2001", PaymentStatus.Pending, FailureReason.Unknown)]
    [InlineData("0003", PaymentStatus.Failed, FailureReason.Canceled)]
    [InlineData("0004", PaymentStatus.Failed, FailureReason.AuthenticationFailed)]
    [InlineData("2003", PaymentStatus.Failed, FailureReason.ProcessingFailed)]
    [InlineData("0999", PaymentStatus.Failed, FailureReason.ProcessingFailed)]
    [InlineData("4051", PaymentStatus.Failed, FailureReason.InsufficientFunds)]
    [InlineData("4005", PaymentStatus.Failed, FailureReason.Declined)]
    public void Response_codes_map_to_a_status_and_a_reason(string respCode, PaymentStatus status, FailureReason reason)
    {
        Assert.Equal(status, TwoCTwoPMapping.ToStatus(respCode));
        if (status == PaymentStatus.Failed)
        {
            Assert.Equal(reason, TwoCTwoPMapping.ToFailure(respCode, "desc")!.Reason);
            Assert.Equal(respCode, TwoCTwoPMapping.ToFailure(respCode, "desc")!.ProviderCode);
        }
    }

    [Fact]
    public async Task A_transaction_not_found_becomes_a_not_found_exception()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["respCode"] = "2002", ["respDesc"] = "Transaction not found",
        }));
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(
            () => gateway.GetChargeAsync(new ChargeRef(null, "order-missing")));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Equal("2002", ex.ProviderCode);
    }

    [Fact]
    public async Task A_refund_is_refused_as_not_supported_without_calling_2c2p()
    {
        var handler = new StubHandler();
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef(null, "order-1"), Money.Thb(50000), "changed mind", "r-1")));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("refund_not_supported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_response_signed_with_the_wrong_secret_is_treated_as_malformed()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(
            new Dictionary<string, object?> { ["respCode"] = "0000", ["webPaymentUrl"] = "https://example/pay" },
            secret: "a-different-secret"));
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(1000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
        }));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task A_plain_unsigned_error_body_is_mapped_by_resp_code_instead_of_malformed()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """{"respCode":"9999","respDesc":"Invalid merchant"}""");
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(1000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
        }));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("9999", ex.ProviderCode);
    }

    [Fact]
    public async Task A_generated_idempotency_key_is_stable_across_retried_attempts()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
            {
                ["respCode"] = "0000", ["webPaymentUrl"] = "https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc",
            }));
        var gateway = BuildGateway(handler);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(1000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
        });

        Assert.Equal(2, handler.Requests.Count);
        var keys = handler.Bodies.Select(b => PayloadOf(b).GetProperty("idempotencyID").GetString()).Distinct();
        Assert.Single(keys);
    }

    [Theory]
    [InlineData(100050, "1000.50")]
    [InlineData(100000, "1000.00")]
    [InlineData(2500, "25.00")]
    [InlineData(25, "0.25")]
    public void Minor_units_become_a_two_place_json_number(long minorUnits, string expected)
    {
        var amount = TwoCTwoPMapping.ToDecimalAmount(Money.Thb(minorUnits));

        Assert.Equal(expected, JsonSerializer.Serialize(amount));   // a number, not a quoted string
    }

    [Theory]
    [InlineData("1000.50", 100050)]
    [InlineData("1000", 100000)]
    [InlineData("0.25", 25)]
    public void A_decimal_amount_comes_back_as_exact_minor_units(string json, long expected)
    {
        Assert.True(TwoCTwoPMapping.TryFromDecimalAmount(JsonDocument.Parse(json).RootElement.GetDecimal(), "THB", out var money));

        Assert.Equal(Money.Thb(expected), money);
    }

    [Fact]
    public void A_currency_whose_minor_unit_is_not_a_hundredth_is_refused()
    {
        // Dividing by 100 is only right for two-decimal currencies; JPY has none and KWD has three.
        var ex = Assert.Throws<PaymentApiException>(() => TwoCTwoPMapping.ToDecimalAmount(Money.From(1000, "JPY")));

        Assert.Equal("currency_not_supported", ex.ProviderCode);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/payment/4.3/#/token/abc")]
    [InlineData("ftp://sandbox-pgw-ui.2c2p.com/x")]
    public async Task A_web_payment_url_that_is_not_an_absolute_http_url_is_a_malformed_response(string url)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["webPaymentUrl"] = url, ["respCode"] = "0000", ["respDesc"] = "Success",
        }));
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(100000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        }));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Theory]
    [InlineData("1e30", "THB")]                         // overflows decimal * 100 and long
    [InlineData("92233720368547758.08", "THB")]         // one satang past long.MaxValue
    [InlineData("-5.00", "THB")]
    [InlineData("10.00", "TH")]
    public async Task An_inquired_amount_money_cannot_hold_is_a_malformed_response(string amount, string currency)
    {
        var claims = $$"""
        { "invoiceNo": "order-1", "amount": {{amount}}, "currencyCode": "{{currency}}", "respCode": "0000", "respDesc": "Success" }
        """;
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(claims)!));
        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef(null, "order-1")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, FailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, FailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, FailureKind.Permission)]
    public async Task A_non_2xx_with_no_2c2p_body_is_classified_by_its_http_status(HttpStatusCode status, FailureKind expected)
    {
        var handler = new StubHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Enqueue(status, "<html><body>Service Unavailable</body></html>");
        }

        var gateway = BuildGateway(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef(null, "order-1")));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal($"HTTP_{(int)status}", ex.ProviderCode);
        Assert.Equal((int)status, ex.HttpStatus);
    }

    private static TwoCTwoPPaymentGateway BuildGateway(StubHandler handler, Action<TwoCTwoPOptions>? configure = null)
    {
        var client = new HttpClient(handler) { BaseAddress = TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Sandbox) };
        var twoCTwoPOptions = new TwoCTwoPOptions { MerchantId = "JT01", SecretKey = Secret };
        configure?.Invoke(twoCTwoPOptions);
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions()));
        return new TwoCTwoPPaymentGateway(new StubClientFactory(client), Options.Create(twoCTwoPOptions), gate);
    }

    private static string ResponseEnvelope(Dictionary<string, object?> claims, string? secret = null) =>
        JsonSerializer.Serialize(new { payload = JwtHs256.Encode(claims, secret ?? Secret) });

    private static JsonElement PayloadOf(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var token = document.RootElement.GetProperty("payload").GetString()!;
        return JwtHs256.DecodePayloadWithoutVerifying(token);
    }

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
