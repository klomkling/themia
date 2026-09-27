using System.Net;

using Themia.Payments;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

/// <summary>A 2xx body that does not hold what the adapter needs is <c>malformed_response</c>, never a raw exception.</summary>
public class BeamMalformedResponseTests
{
    private static CreateChargeRequest Qr() => new()
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    };

    private static CreateChargeRequest Link() => Qr() with { AllowedMethods = [PaymentMethod.Card] };

    public static TheoryData<string> BadDirectChargeBodies => new()
    {
        "<html>502 from a proxy that said 200</html>",
        """{ "actionRequired": "ENCODED_IMAGE" }""",
        """{ "chargeId": "ch_1", "actionRequired": "REDIRECT", "redirect": { "redirectUrl": "/relative/only" } }""",
        """{ "chargeId": "ch_1", "actionRequired": "REDIRECT", "redirect": {} }""",
        """{ "chargeId": "ch_1", "actionRequired": "ENCODED_IMAGE", "encodedImage": { "imageBase64Encoded": "***not base64***" } }""",
        """{ "chargeId": "ch_1", "actionRequired": "ENCODED_IMAGE", "encodedImage": {} }""",
    };

    [Theory]
    [MemberData(nameof(BadDirectChargeBodies))]
    public async Task A_bad_direct_charge_body_is_a_malformed_response(string body)
    {
        var (gateway, _) = Build(body);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(Qr()));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
        Assert.Equal(200, ex.HttpStatus);
    }

    [Fact]
    public async Task A_payment_link_with_a_relative_url_is_a_malformed_response()
    {
        var (gateway, _) = Build("""{ "id": "rGtqz6DafS", "url": "not a url" }""", HttpStatusCode.Created);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(Link()));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task A_created_link_with_a_relative_url_is_a_malformed_response_on_the_beam_client()
    {
        var client = BuildClient(new StubHandler().Enqueue(HttpStatusCode.Created, """{ "id": "rGtqz6DafS", "url": "/m/x" }"""));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.CreatePaymentLinkAsync(new BeamPaymentLinkRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
        }));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task A_read_link_with_a_relative_url_is_a_malformed_response_on_the_beam_client()
    {
        var client = BuildClient(new StubHandler().Enqueue(HttpStatusCode.OK, PaymentLinkGetResponse
            .Replace("https://playground-pay.beamcheckout.com/m/rGtqz6DafS", "/m/rGtqz6DafS", StringComparison.Ordinal)));

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.GetPaymentLinkAsync("rGtqz6DafS"));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Theory]
    [InlineData("-1", "THB")]
    [InlineData("100", "BAHT")]
    [InlineData("100", "T1B")]
    public async Task A_charge_with_an_amount_money_cannot_hold_is_a_malformed_response(string amount, string currency)
    {
        var (gateway, _) = Build($$"""
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "{{currency}}", "amount": {{amount}} }
        """);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef("ch_1", "order-1")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task A_payment_link_with_a_negative_amount_is_a_malformed_response()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""")
            .Enqueue(HttpStatusCode.OK, PaymentLinkGetResponse.Replace("250000", "-5", StringComparison.Ordinal));
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef("rGtqz6DafS", "order-3")));

        Assert.Equal("malformed_response", ex.ProviderCode);
    }
}
