using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Themia.Payments.Beam.Internal;

namespace Themia.Payments.Beam;

/// <summary>
/// Verifies a Beam webhook by HMAC-SHA256 over the exact raw body, then reads the event from it.
/// </summary>
/// <remarks>
/// Pinned to Beam's own published golden vector (docs.beamcheckout.com/webhook-authentication): the same
/// body, key and signature there must verify here, byte for byte.
/// <para>
/// The event name comes from the unsigned <c>X-Beam-Event</c> header — the HMAC covers only the body — so
/// it is reconciled against the signed body before being trusted. For the four modelled event types, the
/// body must agree with the header or the result is <see cref="WebhookOutcome.Malformed"/>:
/// <c>charge.succeeded</c>/<c>charge.failed</c> require a non-empty <c>chargeId</c>, no <c>refundId</c>
/// property, and <c>status</c> exactly <c>SUCCEEDED</c>/<c>FAILED</c> respectively;
/// <c>refund.succeeded</c>/<c>refund.failed</c> require a non-empty <c>refundId</c> and the matching
/// <c>status</c>. Any other header value (including a missing one) is <see cref="PaymentEventType.Other"/>
/// and is not checked for consistency.
/// </para>
/// </remarks>
public sealed class BeamWebhookVerifier : IPaymentWebhookVerifier
{
    private static readonly string[] OccurredAtFields = ["transactionTime", "updatedAt", "createdAt"];

    private readonly IOptions<BeamOptions> options;
    private byte[]? decodedKey;

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">Supplies <see cref="BeamOptions.WebhookHmacKey"/>.</param>
    public BeamWebhookVerifier(IOptions<BeamOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <see cref="BeamOptions.WebhookHmacKey"/> is not a usable key: null, blank, not base64, or shorter than
    /// 16 bytes. This fails closed — a signature is never checked against an empty or undecodable key.
    /// </exception>
    public WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!TryGetHeader(headers, "X-Beam-Signature", out var provided) || string.IsNullOrWhiteSpace(provided))
        {
            return new WebhookVerification(WebhookOutcome.SignatureMissing, null);
        }

        Span<byte> computed = stackalloc byte[32];
        HMACSHA256.HashData(Key(), rawBody, computed);

        Span<byte> presented = stackalloc byte[32];
        if (!Convert.TryFromBase64String(provided, presented, out var written)
            || written != computed.Length
            || !CryptographicOperations.FixedTimeEquals(computed, presented))
        {
            return new WebhookVerification(WebhookOutcome.SignatureMismatch, null);
        }

        TryGetHeader(headers, "X-Beam-Event", out var eventName);
        return ParseEvent(rawBody, eventName);
    }

    /// <summary>Decodes the configured key once; throws rather than ever returning an unusable key.</summary>
    private byte[] Key()
    {
        if (decodedKey is not null)
        {
            return decodedKey;
        }

        if (!BeamWebhookKey.TryDecode(options.Value.WebhookHmacKey, out var key))
        {
            throw new InvalidOperationException(
                $"BeamOptions.WebhookHmacKey must be set to the base64 of at least {BeamWebhookKey.MinimumBytes} bytes to verify webhooks.");
        }

        decodedKey = key;
        return key;
    }

    /// <summary>Finds a header value, matching the name case-insensitively regardless of the dictionary's own comparer.</summary>
    private static bool TryGetHeader(IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static WebhookVerification ParseEvent(ReadOnlySpan<byte> rawBody, string? eventName)
    {
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
            if (root.ValueKind != JsonValueKind.Object || !TryGetOccurredAt(root, out var occurredAt))
            {
                return new WebhookVerification(WebhookOutcome.Malformed, null);
            }

            var type = ToEventType(eventName);
            if (IsModelled(type) && !IsConsistentWithBody(root, type))
            {
                // The unsigned X-Beam-Event header disagrees with the signed body: never trust the header.
                return new WebhookVerification(WebhookOutcome.Malformed, null);
            }

            if (!TryReadAmount(root, out var amount) && IsModelled(type))
            {
                // Claimed but not a value Money.From can accept (wrong JSON type, one side missing, negative,
                // or a malformed currency code).
                // Amount is part of what the consumer acts on for a modelled type, so treat it as tampered.
                return new WebhookVerification(WebhookOutcome.Malformed, null);
            }

            var chargeId = root.TryGetProperty("chargeId", out var chargeIdElement) &&
                chargeIdElement.ValueKind == JsonValueKind.String
                    ? chargeIdElement.GetString()
                    : null;

            var referenceId = root.TryGetProperty("referenceId", out var referenceIdElement) &&
                referenceIdElement.ValueKind == JsonValueKind.String
                    ? referenceIdElement.GetString()
                    : null;

            var status = root.TryGetProperty("status", out var statusElement) &&
                statusElement.ValueKind == JsonValueKind.String
                    ? BeamMapping.ToStatus(statusElement.GetString() ?? "")
                    : PaymentStatus.Pending;

            var paymentEvent = new PaymentEvent(
                type,
                chargeId,
                referenceId,
                amount,
                status,
                occurredAt,
                Encoding.UTF8.GetString(rawBody));

            return new WebhookVerification(WebhookOutcome.Verified, paymentEvent);
        }
    }

    private static bool IsModelled(PaymentEventType type) => type is
        PaymentEventType.ChargeSucceeded or PaymentEventType.ChargeFailed or
        PaymentEventType.RefundSucceeded or PaymentEventType.RefundFailed;

    /// <summary>
    /// Checks the signed body against the unsigned header's claimed type. See the class remarks for the
    /// per-type rules; only called for the four modelled types.
    /// </summary>
    private static bool IsConsistentWithBody(JsonElement root, PaymentEventType type) => type switch
    {
        PaymentEventType.ChargeSucceeded => IsChargeConsistent(root, "SUCCEEDED"),
        PaymentEventType.ChargeFailed => IsChargeConsistent(root, "FAILED"),
        PaymentEventType.RefundSucceeded => IsRefundConsistent(root, "SUCCEEDED"),
        PaymentEventType.RefundFailed => IsRefundConsistent(root, "FAILED"),
        _ => true,
    };

    private static bool IsChargeConsistent(JsonElement root, string expectedStatus) =>
        BeamMapping.TryGetNonEmptyString(root, "chargeId", out _) &&
        !root.TryGetProperty("refundId", out _) &&
        HasStatus(root, expectedStatus);

    private static bool IsRefundConsistent(JsonElement root, string expectedStatus) =>
        BeamMapping.TryGetNonEmptyString(root, "refundId", out _) &&
        HasStatus(root, expectedStatus);

    private static bool HasStatus(JsonElement root, string expectedStatus) =>
        root.TryGetProperty("status", out var statusElement) &&
        statusElement.ValueKind == JsonValueKind.String &&
        string.Equals(statusElement.GetString(), expectedStatus, StringComparison.Ordinal);

    /// <summary>
    /// Reads <c>amount</c>/<c>currency</c> into a <see cref="Money"/>, the same rule as the 2C2P verifier: returns
    /// <see langword="false"/> when either is present with the wrong JSON type, only one of them is present, or
    /// together they do not form a value <see cref="Money.From"/> can accept (a negative amount, or a currency that
    /// is not three ASCII letters) — never lets that throw. Only when <b>both</b> are absent is nothing claimed,
    /// which is not an error: this returns <see langword="true"/> with <paramref name="amount"/> left null.
    /// </summary>
    private static bool TryReadAmount(JsonElement root, out Money? amount)
    {
        amount = null;
        var hasAmount = root.TryGetProperty("amount", out var amountElement);
        var hasCurrency = root.TryGetProperty("currency", out var currencyElement);
        if (!hasAmount && !hasCurrency)
        {
            return true;
        }

        if (!hasAmount || amountElement.ValueKind != JsonValueKind.Number ||
            !hasCurrency || currencyElement.ValueKind != JsonValueKind.String ||
            !amountElement.TryGetInt64(out var minorUnits) ||
            !BeamMapping.TryMoney(minorUnits, currencyElement.GetString(), out var money))
        {
            return false;
        }

        amount = money;
        return true;
    }

    private static bool TryGetOccurredAt(JsonElement root, out DateTimeOffset occurredAt)
    {
        foreach (var propertyName in OccurredAtFields)
        {
            if (root.TryGetProperty(propertyName, out var element) &&
                element.ValueKind == JsonValueKind.String &&
                element.TryGetDateTimeOffset(out var parsed))
            {
                occurredAt = parsed;
                return true;
            }
        }

        occurredAt = default;
        return false;
    }

    private static PaymentEventType ToEventType(string? eventName) => eventName switch
    {
        "charge.succeeded" => PaymentEventType.ChargeSucceeded,
        "charge.failed" => PaymentEventType.ChargeFailed,
        "refund.succeeded" => PaymentEventType.RefundSucceeded,
        "refund.failed" => PaymentEventType.RefundFailed,
        _ => PaymentEventType.Other,
    };
}
