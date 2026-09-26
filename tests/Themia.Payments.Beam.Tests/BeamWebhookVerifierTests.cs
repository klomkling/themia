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
}
