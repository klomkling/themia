using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Themia.Payments.Beam;

/// <summary>
/// Verifies a Beam webhook by HMAC-SHA256 over the exact raw body, then reads the event from it.
/// </summary>
/// <remarks>
/// Pinned to Beam's own published golden vector (docs.beamcheckout.com/webhook-authentication): the same
/// body, key and signature there must verify here, byte for byte.
/// </remarks>
public sealed class BeamWebhookVerifier : IPaymentWebhookVerifier
{
    private static readonly string[] OccurredAtFields = ["transactionTime", "updatedAt", "createdAt"];

    private readonly IOptions<BeamOptions> options;

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">Supplies <see cref="BeamOptions.WebhookHmacKey"/>.</param>
    public BeamWebhookVerifier(IOptions<BeamOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><see cref="BeamOptions.WebhookHmacKey"/> is not set.</exception>
    public WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!TryGetHeader(headers, "X-Beam-Signature", out var provided) || string.IsNullOrWhiteSpace(provided))
        {
            return new WebhookVerification(WebhookOutcome.SignatureMissing, null);
        }

        var key = options.Value.WebhookHmacKey
            ?? throw new InvalidOperationException("BeamOptions.WebhookHmacKey must be set to verify webhooks.");

        Span<byte> computed = stackalloc byte[32];
        HMACSHA256.HashData(Convert.FromBase64String(key), rawBody, computed);

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

            var chargeId = root.TryGetProperty("chargeId", out var chargeIdElement) &&
                chargeIdElement.ValueKind == JsonValueKind.String
                    ? chargeIdElement.GetString()
                    : null;

            var referenceId = root.TryGetProperty("referenceId", out var referenceIdElement) &&
                referenceIdElement.ValueKind == JsonValueKind.String
                    ? referenceIdElement.GetString()
                    : null;

            var amount = TryGetAmount(root, out var money) ? (Money?)money : null;

            var status = root.TryGetProperty("status", out var statusElement) &&
                statusElement.ValueKind == JsonValueKind.String
                    ? BeamMapping.ToStatus(statusElement.GetString() ?? "")
                    : PaymentStatus.Pending;

            var paymentEvent = new PaymentEvent(
                ToEventType(eventName),
                chargeId,
                referenceId,
                amount,
                status,
                occurredAt,
                Encoding.UTF8.GetString(rawBody));

            return new WebhookVerification(WebhookOutcome.Verified, paymentEvent);
        }
    }

    private static bool TryGetAmount(JsonElement root, out Money money)
    {
        if (root.TryGetProperty("amount", out var amountElement) &&
            amountElement.ValueKind == JsonValueKind.Number &&
            amountElement.TryGetInt64(out var minorUnits) &&
            root.TryGetProperty("currency", out var currencyElement) &&
            currencyElement.ValueKind == JsonValueKind.String &&
            currencyElement.GetString() is { Length: > 0 } currency)
        {
            money = Money.From(minorUnits, currency);
            return true;
        }

        money = default;
        return false;
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
