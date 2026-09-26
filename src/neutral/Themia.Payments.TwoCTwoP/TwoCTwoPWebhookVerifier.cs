using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.TwoCTwoP;

/// <summary>Verifies a 2C2P backend notification by its own JWT signature, then reads the event from it.</summary>
/// <remarks>
/// Spec §7b: every 2C2P body is <c>{"payload":"&lt;JWT&gt;"}</c>, HS256-signed with the merchant secret — the
/// signature <b>is</b> the authentication. Unlike Beam, 2C2P sends no header at all: <c>headers</c> is accepted
/// only to satisfy <see cref="IPaymentWebhookVerifier"/> and is never consulted.
/// <para>
/// Outcome boundary: a body that is not JSON, is not a JSON object, or has no string <c>payload</c> property is
/// <see cref="WebhookOutcome.Malformed"/> — the request never carried a checkable signature at all. A string
/// <c>payload</c> that fails <see cref="JwtHs256.TryDecode"/> is always <see cref="WebhookOutcome.SignatureMismatch"/>:
/// <see cref="JwtHs256.TryDecode"/> folds "wrong segment count" / "invalid base64url" / "non-object JSON" / "bad
/// signature" into one <c>false</c>, so a well-formed three-segment token with a bad signature and a token that
/// is not even shaped like a JWT are both reported the same way — a mismatch, not a different parse failure.
/// </para>
/// </remarks>
public sealed class TwoCTwoPWebhookVerifier : IPaymentWebhookVerifier
{
    private readonly IOptions<TwoCTwoPOptions> options;

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">Supplies <see cref="TwoCTwoPOptions.SecretKey"/> and
    /// <see cref="TwoCTwoPOptions.TransactionTimeOffset"/>.</param>
    public TwoCTwoPWebhookVerifier(IOptions<TwoCTwoPOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody.ToArray());
        }
        catch (JsonException)
        {
            return new WebhookVerification(WebhookOutcome.Malformed, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out var payloadElement) ||
                payloadElement.ValueKind != JsonValueKind.String ||
                payloadElement.GetString() is not { Length: > 0 } token)
            {
                return new WebhookVerification(WebhookOutcome.Malformed, null);
            }

            if (!JwtHs256.TryDecode(token, options.Value.SecretKey, out var payload))
            {
                return new WebhookVerification(WebhookOutcome.SignatureMismatch, null);
            }

            return ParseEvent(payload, options.Value.TransactionTimeOffset);
        }
    }

    private static WebhookVerification ParseEvent(JsonElement payload, TimeSpan transactionTimeOffset)
    {
        if (!TwoCTwoPMapping.TryGetNonEmptyString(payload, "invoiceNo", out var invoiceNo) ||
            !TwoCTwoPMapping.TryGetNonEmptyString(payload, "respCode", out var respCode))
        {
            return new WebhookVerification(WebhookOutcome.Malformed, null);
        }

        if (!TryReadAmount(payload, out var amount) ||
            !TryReadOccurredAt(payload, transactionTimeOffset, out var occurredAt))
        {
            return new WebhookVerification(WebhookOutcome.Malformed, null);
        }

        var status = TwoCTwoPMapping.ToStatus(respCode);
        var type = status switch
        {
            PaymentStatus.Succeeded => PaymentEventType.ChargeSucceeded,
            PaymentStatus.Failed => PaymentEventType.ChargeFailed,
            // Pending (0001/2001): PaymentEventType has no ChargePending member. Other is the closest existing
            // one — authentic, just not one of the four this package fully models (same as Beam's unmodelled types).
            _ => PaymentEventType.Other,
        };

        var paymentEvent = new PaymentEvent(type, invoiceNo, invoiceNo, amount, status, occurredAt, payload.GetRawText());
        return new WebhookVerification(WebhookOutcome.Verified, paymentEvent);
    }

    /// <summary>
    /// Reads <c>amount</c>/<c>currencyCode</c> into a <see cref="Money"/>. Returns <see langword="false"/> when
    /// either is present with the wrong JSON type, or does not form a value
    /// <see cref="TwoCTwoPMapping.TryFromDecimalAmount"/> can accept — never lets that throw. Only when
    /// <b>both</b> are absent is nothing claimed, which is not an error (<see cref="PaymentEvent.Amount"/> is
    /// nullable for exactly this case).
    /// </summary>
    private static bool TryReadAmount(JsonElement payload, out Money? amount)
    {
        amount = null;
        var hasAmount = payload.TryGetProperty("amount", out var amountElement);
        var hasCurrency = payload.TryGetProperty("currencyCode", out var currencyElement);
        if (!hasAmount && !hasCurrency)
        {
            return true;
        }

        if (!hasAmount || amountElement.ValueKind != JsonValueKind.Number ||
            !hasCurrency || currencyElement.ValueKind != JsonValueKind.String ||
            !amountElement.TryGetDecimal(out var decimalAmount) ||
            !TwoCTwoPMapping.TryFromDecimalAmount(decimalAmount, currencyElement.GetString(), out var money))
        {
            return false;
        }

        amount = money;
        return true;
    }

    /// <summary>
    /// Reads and parses <c>transactionDateTime</c> (2C2P's own <c>"yyyyMMddHHmmss"</c> timestamp, offset by
    /// <paramref name="transactionTimeOffset"/> since 2C2P's own format carries none). Mandatory per the 2C2P
    /// notification spec: absent, non-string, empty, or unparsable is all malformed — this verifier never
    /// fabricates a receipt time as a substitute.
    /// </summary>
    private static bool TryReadOccurredAt(JsonElement payload, TimeSpan transactionTimeOffset, out DateTimeOffset occurredAt)
    {
        if (!payload.TryGetProperty("transactionDateTime", out var element) || element.ValueKind != JsonValueKind.String)
        {
            occurredAt = default;
            return false;
        }

        return TwoCTwoPMapping.TryParseTransactionDateTime(element.GetString(), transactionTimeOffset, out occurredAt);
    }
}
