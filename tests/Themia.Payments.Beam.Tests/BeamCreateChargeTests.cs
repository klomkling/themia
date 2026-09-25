using System.Net;
using System.Text.Json;
using Themia.Payments;
using Themia.Payments.Beam;
using Xunit;

namespace Themia.Payments.Beam.Tests;

public class BeamCreateChargeTests
{
    [Fact]
    public async Task A_qr_charge_sends_minor_units_and_the_reference_id()
    {
        var (gateway, handler) = BeamTestHost.Build(BeamTestHost.EncodedImageResponse);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order_190822",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        });

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(10000, sent.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("THB", sent.RootElement.GetProperty("currency").GetString());
        Assert.Equal("order_190822", sent.RootElement.GetProperty("referenceId").GetString());
        Assert.Equal("QR_PROMPT_PAY",
            sent.RootElement.GetProperty("paymentMethod").GetProperty("paymentMethodType").GetString());
        Assert.Equal("/api/v1/charges", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_encoded_image_response_becomes_a_show_qr_action()
    {
        var (gateway, _) = BeamTestHost.Build(BeamTestHost.EncodedImageResponse);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order_190822",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        });

        var qr = Assert.IsType<NextAction.ShowQr>(creation.Action);
        Assert.Equal("hello"u8.ToArray(), qr.ImagePng);
        Assert.Equal("00020101", qr.RawPayload);
        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.Equal("ch_2xTsz7Qit55pahSvKfJG3UMkpFQ", creation.ChargeId);
    }

    [Fact]
    public async Task A_redirect_response_becomes_a_redirect_action()
    {
        var (gateway, _) = BeamTestHost.Build("""
        { "actionRequired": "REDIRECT", "chargeId": "ch_1", "redirect": { "redirectUrl": "https://pay.example/1" } }
        """);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(500),
            ReferenceId = "order-2",
            AllowedMethods = [PaymentMethod.Card],
        });

        Assert.Equal(new Uri("https://pay.example/1"), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
    }

    [Fact]
    public async Task Two_methods_become_one_payment_link_with_exactly_those_groups_enabled()
    {
        var (gateway, handler) = BeamTestHost.Build(BeamTestHost.PaymentLinkResponse);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(250000),
            ReferenceId = "order-3",
            AllowedMethods = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
        });

        Assert.Equal("/api/v1/payment-links", handler.Requests[0].RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        var order = sent.RootElement.GetProperty("order");
        Assert.Equal(250000, order.GetProperty("netAmount").GetInt64());
        Assert.Equal("order-3", order.GetProperty("referenceId").GetString());

        var settings = sent.RootElement.GetProperty("linkSettings");
        Assert.True(settings.GetProperty("qrPromptPay").GetProperty("isEnabled").GetBoolean());
        Assert.True(settings.GetProperty("card").GetProperty("isEnabled").GetBoolean());
        // Sent explicitly as false, never omitted: Beam treats an omitted group as disabled today, but a
        // payload that says what it means does not depend on that staying true.
        Assert.False(settings.GetProperty("mobileBanking").GetProperty("isEnabled").GetBoolean());
        Assert.False(settings.GetProperty("eWallets").GetProperty("isEnabled").GetBoolean());
        Assert.False(settings.GetProperty("cardInstallments").GetProperty("isEnabled").GetBoolean());
        Assert.False(settings.GetProperty("buyNowPayLater").GetProperty("isEnabled").GetBoolean());

        Assert.Equal("rGtqz6DafS", creation.ChargeId);
        Assert.Equal(new Uri("https://playground-pay.beamcheckout.com/m/rGtqz6DafS"),
            Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.Equal(PaymentStatus.Pending, creation.Status);
    }

    [Fact]
    public async Task A_wallet_only_request_becomes_a_payment_link_offering_only_ewallets()
    {
        // Beam has no single charge type for "e-wallet"; the group exists only on a link.
        var (gateway, handler) = BeamTestHost.Build(BeamTestHost.PaymentLinkResponse);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(250000),
            ReferenceId = "order-3",
            AllowedMethods = [PaymentMethod.Wallet],
        });

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        var settings = sent.RootElement.GetProperty("linkSettings");
        Assert.True(settings.GetProperty("eWallets").GetProperty("isEnabled").GetBoolean());
        Assert.False(settings.GetProperty("qrPromptPay").GetProperty("isEnabled").GetBoolean());
        Assert.False(settings.GetProperty("card").GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_beam_error_becomes_a_typed_exception()
    {
        var (gateway, _) = BeamTestHost.Build("""
        { "code": 401, "message": "invalid authentication credentials",
          "error": { "errorCode": "INVALID_CREDENTIALS_ERROR", "errorMessage": "invalid authentication credentials" } }
        """, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(500),
            ReferenceId = "order-4",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        }));

        Assert.Equal(FailureKind.Authentication, ex.Kind);
        Assert.Equal("INVALID_CREDENTIALS_ERROR", ex.ProviderCode);
        Assert.Equal(401, ex.HttpStatus);
    }
}
