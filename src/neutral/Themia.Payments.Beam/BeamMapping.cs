using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Payments.Beam;

/// <summary>Every Beam &lt;-&gt; <c>Themia.Payments</c> translation lives here: methods, actions, and errors.</summary>
internal static class BeamMapping
{
    /// <summary>Shared request-serialization options: omit unset properties rather than send them as null.</summary>
    public static readonly JsonSerializerOptions SerializeOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// The Beam charge type for a method that can be charged directly, or null when it cannot. Only
    /// <see cref="PaymentMethod.QrPromptPay"/> qualifies in v1: a direct <c>CARD</c> charge needs card data
    /// or a token, and tokenization/3DS are out of scope (spec §8), so <see cref="PaymentMethod.Card"/>
    /// always routes through a payment link instead, where Beam's hosted page collects the card.
    /// <see cref="PaymentMethod.MobileBanking"/> and <see cref="PaymentMethod.Wallet"/> exist only as
    /// payment-link groups and were never direct-chargeable.
    /// </summary>
    public static string? ToDirectChargeType(PaymentMethod method) => method switch
    {
        PaymentMethod.QrPromptPay => "QR_PROMPT_PAY",
        _ => null,
    };

    /// <summary>The <c>linkSettings</c> group for a method. Every method has one.</summary>
    public static string ToLinkGroup(PaymentMethod method) => method switch
    {
        PaymentMethod.QrPromptPay => "qrPromptPay",
        PaymentMethod.Card => "card",
        PaymentMethod.MobileBanking => "mobileBanking",
        PaymentMethod.Wallet => "eWallets",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    /// <summary>
    /// A <c>linkSettings</c> object that enables exactly <paramref name="allowed"/> and disables every other
    /// group explicitly — sending the object replaces the account defaults, and an omitted group is disabled.
    /// </summary>
    public static Dictionary<string, object> ToLinkSettings(IReadOnlyList<PaymentMethod> allowed)
    {
        var settings = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var method in Enum.GetValues<PaymentMethod>())
        {
            settings[ToLinkGroup(method)] = new { isEnabled = allowed.Contains(method) };
        }

        settings["cardInstallments"] = new { isEnabled = false };
        settings["buyNowPayLater"] = new { isEnabled = false };
        return settings;
    }

    /// <summary>Maps a direct-charge response's <c>actionRequired</c> to a <see cref="NextAction"/>.</summary>
    /// <param name="root">The response body.</param>
    /// <param name="httpStatus">The response's HTTP status, carried only for a malformed-body exception.</param>
    public static NextAction ToNextAction(JsonElement root, int httpStatus)
    {
        var actionRequired = root.TryGetProperty("actionRequired", out var actionElement) &&
            actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

        return actionRequired switch
        {
            "REDIRECT" => new NextAction.Redirect(
                new Uri(root.GetProperty("redirect").GetProperty("redirectUrl").GetString()!)),
            "ENCODED_IMAGE" => ToShowQr(root.GetProperty("encodedImage"), httpStatus),
            _ => new NextAction.None(),
        };
    }

    /// <summary>Maps a Beam charge or payment-link status to a normalized <see cref="PaymentStatus"/>.</summary>
    public static PaymentStatus ToStatus(string status) => status switch
    {
        "SUCCEEDED" => PaymentStatus.Succeeded,
        "FAILED" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending,
    };

    /// <summary>Maps a charge's <c>failureCode</c> to a normalized <see cref="PaymentFailure"/>, or null when there is none.</summary>
    public static PaymentFailure? ToFailure(string? failureCode, string? message)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return null;
        }

        var reason = failureCode switch
        {
            "CH_INSUFFICIENT_FUNDS" => FailureReason.InsufficientFunds,
            "CH_AUTHENTICATION_FAILED" => FailureReason.AuthenticationFailed,
            "CH_PROCESSING_FAILED" => FailureReason.ProcessingFailed,
            _ when failureCode.StartsWith("CH_CARD_", StringComparison.Ordinal) => FailureReason.Declined,
            _ => FailureReason.Unknown,
        };

        return new PaymentFailure(reason, failureCode, message);
    }

    /// <summary>Reads a required non-empty string property, without throwing on a missing or malformed one.</summary>
    public static bool TryGetNonEmptyString(JsonElement root, string propertyName, out string value)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            element.GetString() is { Length: > 0 } nonEmpty)
        {
            value = nonEmpty;
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>Maps a Beam error code to a normalized failure kind, falling back on the HTTP status.</summary>
    public static FailureKind ToFailureKind(int httpStatus, string errorCode) => errorCode switch
    {
        "INVALID_CREDENTIALS_ERROR" => FailureKind.Authentication,
        "API_VALIDATION_ERROR" or "INVALID_JSON_ERROR" or "INVALID_XML_ERROR" => FailureKind.Validation,
        "NOT_FOUND_ERROR" => FailureKind.NotFound,
        "NO_PERMISSION_ERROR" or "OPERATION_NOT_ALLOWED_ERROR" => FailureKind.Permission,
        "TOO_MANY_REQUESTS_ERROR" => FailureKind.RateLimited,
        _ => httpStatus >= 500 ? FailureKind.Transient : FailureKind.Unknown,
    };

    /// <summary>
    /// Builds the typed exception for a non-2xx response: the provider code is <c>error.errorCode</c> when
    /// the body parses and carries one, or <c>"HTTP_&lt;status&gt;"</c> otherwise — which also lands on
    /// <see cref="ToFailureKind"/>'s status-based fallback, since it never matches a known Beam code.
    /// </summary>
    public static PaymentApiException ToApiException(HttpStatusCode status, JsonElement? errorBody)
    {
        var code = errorBody is { } root &&
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("errorCode", out var errorCodeElement) &&
            errorCodeElement.ValueKind == JsonValueKind.String &&
            errorCodeElement.GetString() is { Length: > 0 } value
                ? value
                : $"HTTP_{(int)status}";

        return new PaymentApiException(ToFailureKind((int)status, code), code, (int)status);
    }

    /// <summary>Parses a response body as JSON, tolerating one that does not parse at all.</summary>
    public static async Task<(bool Parsed, JsonElement Root)> TryParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (true, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (false, default);
        }
    }

    private static NextAction.ShowQr ToShowQr(JsonElement encodedImage, int httpStatus)
    {
        var imageBase64 = encodedImage.GetProperty("imageBase64Encoded").GetString()!;
        var rawPayload = encodedImage.TryGetProperty("rawData", out var rawDataElement) &&
            rawDataElement.ValueKind == JsonValueKind.String
                ? rawDataElement.GetString()
                : null;

        DateTimeOffset? expiry = null;
        if (encodedImage.TryGetProperty("expiry", out var expiryElement) &&
            expiryElement.ValueKind == JsonValueKind.String)
        {
            if (!expiryElement.TryGetDateTimeOffset(out var parsedExpiry))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", httpStatus);
            }

            expiry = parsedExpiry;
        }

        return new NextAction.ShowQr(Convert.FromBase64String(imageBase64), rawPayload, expiry);
    }
}
