using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.Beam;
using Xunit;

namespace Themia.Payments.Beam.Tests;

public class BeamWebhookVerifierTests
{
    private const string Key = "KOFELguf5L1ltuDlkDHGUkPPnQhrgYYijTR4Fqh7APc=";
    private const string Signature = "1XzWtJHZ9Y1tmjkA/XZUIn1ZHrUQp1d0Ms0oDQfJBto=";

    private static byte[] Vector() => File.ReadAllBytes("Fixtures/beam-webhook-vector.json");

    private static BeamWebhookVerifier Verifier() =>
        new(Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k", WebhookHmacKey = Key }));

    private static Dictionary<string, string> Headers(string signature, string eventName = "charge.succeeded") =>
        new(StringComparer.OrdinalIgnoreCase) { ["X-Beam-Signature"] = signature, ["X-Beam-Event"] = eventName };

    /// <summary>Signs an arbitrary test body with the same key/algorithm as the verifier, for cases the
    /// golden fixture cannot cover. Never mutates the golden fixture itself.</summary>
    private static (byte[] Body, string Signature) Sign(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(Key), body));
        return (body, signature);
    }

    [Fact]
    public void The_published_vector_verifies()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
        Assert.Equal("order#10001", result.Event.ReferenceId);
        Assert.Equal(Money.Thb(3000000), result.Event.Amount);
    }

    [Fact]
    public void The_published_vector_carries_the_paid_method_and_source_id()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature));

        Assert.Equal(PaymentMethod.Card, result.Event!.PaidMethod);
        Assert.Equal("57Iot6c11o", result.Event.SourceId);
    }

    [Theory]
    [InlineData("QR_PROMPT_PAY", PaymentMethod.QrPromptPay)]
    [InlineData("CARD_INSTALLMENTS", PaymentMethod.Card)]
    [InlineData("ALIPAY", PaymentMethod.Wallet)]
    public void A_failed_charge_event_maps_the_method_too(string methodType, PaymentMethod expected)
    {
        var (body, signature) = Sign(
            $$"""{"chargeId":"c","referenceId":"r","status":"FAILED","currency":"THB","amount":100,"sourceId":"plink_x","paymentMethod":{"paymentMethodType":"{{methodType}}"},"transactionTime":"2025-07-23T10:16:12Z"}""");

        var result = Verifier().Verify(body, Headers(signature, "charge.failed"));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(expected, result.Event!.PaidMethod);
        Assert.Equal("plink_x", result.Event.SourceId);
    }

    [Fact]
    public void An_unmapped_method_and_a_missing_source_leave_both_null()
    {
        var (body, signature) = Sign(
            """{"chargeId":"c","referenceId":"r","status":"SUCCEEDED","currency":"THB","amount":100,"paymentMethod":{"paymentMethodType":"SOMETHING_NEW"},"transactionTime":"2025-07-23T10:16:12Z"}""");

        var result = Verifier().Verify(body, Headers(signature));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Null(result.Event!.PaidMethod);
        Assert.Null(result.Event.SourceId);
    }

    [Fact]
    public void One_changed_byte_in_the_body_fails()
    {
        var tampered = Vector();
        tampered[^2] = (byte)(tampered[^2] ^ 0x01);

        Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier().Verify(tampered, Headers(Signature)).Outcome);
    }

    [Fact]
    public void A_body_that_was_parsed_and_re_serialized_no_longer_verifies()
    {
        // The trap this interface takes bytes to avoid: System.Text.Json round-tripping changes whitespace,
        // so a pipeline that binds the model before verifying will reject every real webhook.
        using var document = JsonDocument.Parse(Vector());
        var reserialized = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);

        Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier().Verify(reserialized, Headers(Signature)).Outcome);
    }

    [Fact]
    public void A_missing_signature_header_is_its_own_outcome()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Beam-Event"] = "charge.succeeded" };

        Assert.Equal(WebhookOutcome.SignatureMissing, Verifier().Verify(Vector(), headers).Outcome);
    }

    [Fact]
    public void An_event_this_package_does_not_model_is_authentic_but_typed_as_other()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature, "bolt_intent.paid"));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.Other, result.Event!.Type);
    }

    [Fact]
    public void A_negative_signed_amount_is_malformed_not_an_exception()
    {
        var (body, signature) = Sign(
            """{"chargeId":"ch_1","status":"SUCCEEDED","amount":-100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Theory]
    [InlineData("\"amount\":\"100\",\"currency\":\"THB\"")]   // amount as a string
    [InlineData("\"amount\":100,\"currency\":764")]           // currency as a number
    [InlineData("\"amount\":100")]                            // an amount with no currency
    [InlineData("\"currency\":\"THB\"")]                       // a currency with no amount
    public void A_signed_amount_of_the_wrong_shape_is_malformed_like_2c2p(string amountFields)
    {
        // Only when both are absent is no amount claimed; a claim that cannot form Money is never dropped silently.
        var (body, signature) = Sign(
            $$"""{"chargeId":"ch_1","status":"SUCCEEDED",{{amountFields}},"createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void An_invalid_signed_currency_code_is_malformed_not_an_exception()
    {
        var (body, signature) = Sign(
            """{"chargeId":"ch_1","status":"SUCCEEDED","amount":100,"currency":"BAHT","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_charge_failed_body_claimed_as_charge_succeeded_is_malformed()
    {
        var (body, signature) = Sign(
            """{"chargeId":"ch_1","status":"FAILED","amount":100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature, "charge.succeeded"));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_refund_body_claimed_as_charge_succeeded_is_malformed()
    {
        // A refund body carries a chargeId and status SUCCEEDED too; only the refundId rule catches this.
        var (body, signature) = Sign(
            """{"chargeId":"ch_1","refundId":"rf_1","status":"SUCCEEDED","amount":100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature, "charge.succeeded"));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_charge_succeeded_claim_with_no_status_in_the_body_is_malformed()
    {
        var (body, signature) = Sign(
            """{"chargeId":"ch_1","amount":100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature, "charge.succeeded"));

        Assert.Equal(WebhookOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void A_consistent_refund_succeeded_body_verifies()
    {
        var (body, signature) = Sign(
            """{"refundId":"rf_1","status":"SUCCEEDED","amount":100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature, "refund.succeeded"));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.RefundSucceeded, result.Event!.Type);
    }

    [Theory]
    [InlineData("refund.succeeded", "SUCCEEDED", PaymentEventType.RefundSucceeded)]
    [InlineData("refund.failed", "FAILED", PaymentEventType.RefundFailed)]
    public void A_refund_event_carries_the_refund_id_and_the_refunded_amount(string eventName, string status, PaymentEventType type)
    {
        var (body, signature) = Sign(
            $$"""{"chargeId":"ch_1","refundId":"rf_1","status":"{{status}}","amount":100,"currency":"THB","createdAt":"2025-01-01T00:00:00Z"}""");

        var result = Verifier().Verify(body, Headers(signature, eventName));

        Assert.Equal(type, result.Event!.Type);
        Assert.Equal("rf_1", result.Event.RefundId);
        Assert.Equal(Money.Thb(100), result.Event.Amount);
    }

    [Fact]
    public void A_charge_event_has_no_refund_id()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature, "charge.succeeded"));

        Assert.Null(result.Event!.RefundId);
    }

    [Fact]
    public void The_golden_vector_stays_verified_as_charge_succeeded()
    {
        // Confirms the golden body itself satisfies the new consistency rule: chargeId, no refundId,
        // status SUCCEEDED.
        var result = Verifier().Verify(Vector(), Headers(Signature, "charge.succeeded"));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive_even_with_an_ordinal_dictionary()
    {
        // The caller is not required to hand in a case-insensitive dictionary; the verifier must not rely on it.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-beam-signature"] = Signature,
            ["x-beam-event"] = "charge.succeeded",
        };

        var result = Verifier().Verify(Vector(), headers);

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AAECAwQFBgc=")] // 8 bytes: too short to be a real key
    public void A_verifier_built_around_an_unusable_key_never_verifies_a_forged_signature(string key)
    {
        // Constructed directly, bypassing the DI validation: an empty key must fail closed, not HMAC with 0 bytes.
        var verifier = new BeamWebhookVerifier(
            Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k", WebhookHmacKey = key }));
        var body = Vector();
        var decoded = Convert.TryFromBase64String(key, new byte[64], out var written) ? written : 0;
        var forgeKey = Convert.FromBase64String(decoded > 0 ? key : "");
        var forged = Convert.ToBase64String(HMACSHA256.HashData(forgeKey, body));

        Assert.Throws<InvalidOperationException>(() => verifier.Verify(body, Headers(forged)));
    }
}
