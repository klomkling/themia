using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

public class TwoCTwoPWebhookVerifierTests
{
    private static TwoCTwoPWebhookVerifier Verifier(string secret) =>
        new(Options.Create(new TwoCTwoPOptions { MerchantId = "m", SecretKey = secret }));

    private static byte[] Envelope(string token) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { payload = token }));

    [Fact]
    public void A_backend_notification_verifies_by_its_own_jwt_signature_with_no_header()
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = JwtHs256.Encode(new Dictionary<string, object?>
            {
                ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
                ["respCode"] = "0000", ["respDesc"] = "Success",
            }, "secret"),
        }));

        var result = Verifier("secret").Verify(body, new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
        Assert.Equal("order-1", result.Event.ReferenceId);
        Assert.Equal("order-1", result.Event.ChargeId);
        Assert.Equal(Money.Thb(100000), result.Event.Amount);
        Assert.Equal(PaymentStatus.Succeeded, result.Event.Status);
    }

    [Fact]
    public void A_notification_signed_with_another_secret_is_a_mismatch()
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = JwtHs256.Encode(new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0000" }, "attacker"),
        }));

        Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier("secret").Verify(body, new Dictionary<string, string>()).Outcome);
    }

    [Fact]
    public void Non_json_bytes_are_malformed()
    {
        var body = Encoding.UTF8.GetBytes("not json at all");

        Assert.Equal(WebhookOutcome.Malformed, Verifier("secret").Verify(body, new Dictionary<string, string>()).Outcome);
    }

    [Fact]
    public void An_empty_object_with_no_payload_is_malformed()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        Assert.Equal(WebhookOutcome.Malformed, Verifier("secret").Verify(body, new Dictionary<string, string>()).Outcome);
    }

    [Fact]
    public void A_non_string_payload_is_malformed()
    {
        var body = Encoding.UTF8.GetBytes("""{"payload":5}""");

        Assert.Equal(WebhookOutcome.Malformed, Verifier("secret").Verify(body, new Dictionary<string, string>()).Outcome);
    }

    [Fact]
    public void A_verified_payload_missing_invoiceNo_is_malformed()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["respCode"] = "0000" }, "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_pending_respCode_verifies_as_an_unmodelled_event()
    {
        // 2C2P's 0001/2001 pending codes have no ChargePending member in PaymentEventType; the closest
        // existing member is Other — authentic, just not one of the four this package fully models.
        var token = JwtHs256.Encode(
            new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0001" }, "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.Other, result.Event!.Type);
        Assert.Equal(PaymentStatus.Pending, result.Event.Status);
    }

    [Fact]
    public void A_failed_respCode_maps_to_charge_failed()
    {
        var token = JwtHs256.Encode(
            new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0003", ["respDesc"] = "Cancelled" },
            "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeFailed, result.Event!.Type);
        Assert.Equal(PaymentStatus.Failed, result.Event.Status);
    }

    [Fact]
    public void A_signed_amount_with_the_wrong_type_is_malformed_not_an_exception()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["respCode"] = "0000", ["amount"] = "not-a-number", ["currencyCode"] = "THB",
        }, "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void An_invalid_signed_currency_code_is_malformed_not_an_exception()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["respCode"] = "0000", ["amount"] = 100.00m, ["currencyCode"] = "BAHT",
        }, "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_verified_payload_with_no_amount_at_all_still_verifies_with_a_null_amount()
    {
        var token = JwtHs256.Encode(
            new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0000" }, "secret");

        var result = Verifier("secret").Verify(Envelope(token), new Dictionary<string, string>());

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Null(result.Event!.Amount);
    }

    [Fact]
    public void Headers_are_never_consulted_2c2p_uses_none()
    {
        var token = JwtHs256.Encode(
            new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0000" }, "secret");

        // A header bag that would mislead a header-driven verifier (as Beam's would be) must have no effect.
        var headers = new Dictionary<string, string> { ["X-Beam-Event"] = "charge.failed" };
        var result = Verifier("secret").Verify(Envelope(token), headers);

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
    }
}
