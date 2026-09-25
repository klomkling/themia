namespace Themia.Payments;

/// <summary>Checks that a webhook came from the provider, and reads it.</summary>
/// <remarks>
/// Takes <b>bytes, not a parsed object</b>: providers sign the exact body they sent, so anything that
/// deserializes and re-serializes before verifying will mismatch. An ASP.NET Core host must call
/// <c>EnableBuffering()</c> and read the body before model binding.
/// <para>
/// Verification proves origin, not freshness. Beam's signature covers the body with no timestamp and no
/// nonce, so a captured request replays for ever; deduplication is the consumer's, on (charge id, status).
/// </para>
/// </remarks>
public interface IPaymentWebhookVerifier
{
    /// <summary>Verifies and reads a webhook.</summary>
    /// <param name="rawBody">The body exactly as received.</param>
    /// <param name="headers">The request headers, matched case-insensitively by the implementation.</param>
    /// <returns>The outcome, and the event when authentic.</returns>
    WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers);
}
