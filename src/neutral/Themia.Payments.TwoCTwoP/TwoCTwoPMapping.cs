using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.TwoCTwoP;

/// <summary>Every 2C2P &lt;-&gt; <c>Themia.Payments</c> translation lives here: channels, amounts, statuses, and errors.</summary>
internal static class TwoCTwoPMapping
{
    /// <summary>Currencies whose minor unit is a hundredth — the only ones <see cref="ToDecimalAmount"/> accepts.</summary>
    /// <summary>The largest decimal amount whose minor units still fit in a <see cref="long"/>.</summary>
    private const decimal MaxDecimalAmount = long.MaxValue / 100m;

    private static readonly HashSet<string> TwoDecimalCurrencies = new(StringComparer.Ordinal) { "THB", "USD", "SGD", "MYR", "EUR" };

    /// <summary>The 2C2P <c>paymentChannel</c> group code for a method (developer.2c2p.com/docs/reference-payment-channels).</summary>
    public static string ToChannelCode(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => "CC",
        PaymentMethod.QrPromptPay => "THQR",
        PaymentMethod.MobileBanking => "DEEPLINK",
        PaymentMethod.Wallet => "EWALLET",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    /// <summary>Maps a 2C2P <c>respCode</c> to a normalized <see cref="PaymentStatus"/>. Fails closed: any code
    /// this package does not recognize as succeeded or pending is reported as failed, never succeeded.</summary>
    public static PaymentStatus ToStatus(string respCode) => respCode switch
    {
        "0000" => PaymentStatus.Succeeded,
        "0001" or "2001" => PaymentStatus.Pending,
        _ => PaymentStatus.Failed,
    };

    /// <summary>Maps a 2C2P <c>respCode</c> to a normalized <see cref="PaymentFailure"/>, or null for a
    /// succeeded or pending code. Fails closed: an unrecognized code lands on <see cref="FailureReason.ProcessingFailed"/>,
    /// never on a reason that implies the payment could still be retried the same way.</summary>
    public static PaymentFailure? ToFailure(string respCode, string? respDesc)
    {
        var reason = respCode switch
        {
            "0000" or "0001" or "2001" => (FailureReason?)null,
            "0003" => FailureReason.Canceled,
            "0004" => FailureReason.AuthenticationFailed,
            "4051" => FailureReason.InsufficientFunds,
            "4005" => FailureReason.Declined,
            _ => FailureReason.ProcessingFailed,
        };

        return reason is { } value ? new PaymentFailure(value, respCode, respDesc) : null;
    }

    /// <summary>Maps a 2C2P <c>respCode</c> that 2C2P itself treated as a request-level rejection (a plain,
    /// unsigned error body) to a <see cref="FailureKind"/>. Falls back on the HTTP status, then <see cref="FailureKind.Unknown"/>.</summary>
    public static FailureKind ToFailureKind(int httpStatus, string respCode) => respCode switch
    {
        "0004" => FailureKind.Authentication,
        "2002" => FailureKind.NotFound,
        _ => httpStatus >= 500 ? FailureKind.Transient : FailureKind.Unknown,
    };

    /// <summary>Minor units as the two-place decimal 2C2P expects, e.g. 100050 → 1000.50.</summary>
    /// <remarks>
    /// <c>minorUnits / 100m</c> alone is not enough: decimal division yields the smallest scale that is exact, so
    /// 100050 becomes <c>1000.5</c> and 100000 becomes <c>1000</c>, and System.Text.Json writes the scale it is
    /// given. Adding <c>0.00m</c> raises the scale to two — decimal addition keeps the larger operand scale — so
    /// the value serializes as <c>1000.50</c>, still a JSON number. Checked on net10.0.
    /// </remarks>
    /// <exception cref="PaymentApiException"><paramref name="money"/>'s currency is not two-decimal.</exception>
    public static decimal ToDecimalAmount(Money money)
    {
        if (!TwoDecimalCurrencies.Contains(money.Currency))
        {
            throw new PaymentApiException(FailureKind.Validation, "currency_not_supported", httpStatus: 0,
                $"The 2C2P adapter converts two-decimal currencies only; {money.Currency} is not one of them.");
        }

        return (money.MinorUnits / 100m) + 0.00m;
    }

    /// <summary>
    /// A 2C2P decimal amount back into minor units, without throwing: <see langword="false"/> for a negative
    /// amount, one too large for <see cref="long"/> minor units, or a currency that is not three ASCII letters.
    /// </summary>
    public static bool TryFromDecimalAmount(decimal amount, string? currency, out Money money)
    {
        if (amount < 0 || amount > MaxDecimalAmount || currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
        {
            money = default;
            return false;
        }

        money = Money.From(decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.ToEven)), currency);
        return true;
    }

    /// <summary>Parses an absolute <c>http</c>/<c>https</c> URL, without throwing. Scheme-checked, not just
    /// <see cref="UriKind.Absolute"/>: on Unix a rooted path parses as an absolute <c>file://</c> URI.</summary>
    public static bool TryParseHttpUrl(string? value, [NotNullWhen(true)] out Uri? url)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            url = parsed;
            return true;
        }

        url = null;
        return false;
    }

    /// <summary>
    /// Parses 2C2P's <c>transactionDateTime</c> — always <c>"yyyyMMddHHmmss"</c> (e.g. "20260927153000"), per the
    /// backend-notification and payment-inquiry parameter references. 2C2P's own timestamp carries no offset and
    /// does not document its zone, so the caller supplies one (<see cref="TwoCTwoPOptions.TransactionTimeOffset"/>).
    /// </summary>
    internal static bool TryParseTransactionDateTime(string? value, TimeSpan offset, out DateTimeOffset result)
    {
        if (DateTime.TryParseExact(
                value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            result = new DateTimeOffset(parsed, offset);
            return true;
        }

        result = default;
        return false;
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

    /// <summary>
    /// Verifies and decodes <c>root.payload</c> (spec §7b: every 2C2P response is <c>{"payload": "&lt;JWT&gt;"}</c>).
    /// False when there is no string <c>payload</c> property, or the JWT does not verify with <paramref name="secret"/> —
    /// a caller must never parse an unverified payload into a result.
    /// </summary>
    public static bool TryReadVerifiedPayload(JsonElement root, string secret, out JsonElement payload)
    {
        payload = default;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var payloadElement) &&
            payloadElement.ValueKind == JsonValueKind.String &&
            payloadElement.GetString() is { Length: > 0 } token &&
            JwtHs256.TryDecode(token, secret, out payload);
    }

    /// <summary>
    /// 2C2P reports some request-level rejections (bad merchant, bad signature) as a plain, unsigned
    /// <c>{"respCode":..,"respDesc":..}</c> body instead of the usual signed <c>payload</c> envelope.
    /// </summary>
    public static bool TryReadPlainError(JsonElement root, out string respCode, out string? respDesc)
    {
        if (TryGetNonEmptyString(root, "respCode", out respCode))
        {
            respDesc = TryGetNonEmptyString(root, "respDesc", out var desc) ? desc : null;
            return true;
        }

        respDesc = null;
        return false;
    }

    /// <summary>Maps a verified payment-inquiry payload to a <see cref="Charge"/>.</summary>
    /// <param name="verified">The verified <c>payload</c> JWT contents.</param>
    /// <param name="requestedInvoiceNo">The invoice this inquiry asked for, used when the response omits its own.</param>
    /// <param name="respCode">The response's <c>respCode</c>, already read by the caller.</param>
    /// <param name="httpStatus">The response's HTTP status, carried only for a malformed-body exception.</param>
    /// <param name="transactionTimeOffset">The UTC offset to read a present <c>transactionDateTime</c> with
    /// (<see cref="TwoCTwoPOptions.TransactionTimeOffset"/>).</param>
    /// <exception cref="PaymentApiException">The response is missing or misshapes <c>amount</c> or
    /// <c>currencyCode</c> (including a negative, overflowing or non-ISO value), or has a present, non-empty <c>transactionDateTime</c> that does not parse.</exception>
    public static Charge ToCharge(
        JsonElement verified, string requestedInvoiceNo, string respCode, int httpStatus, TimeSpan transactionTimeOffset)
    {
        var invoiceNo = TryGetNonEmptyString(verified, "invoiceNo", out var invoiceFromResponse)
            ? invoiceFromResponse
            : requestedInvoiceNo;

        if (!verified.TryGetProperty("amount", out var amountElement) ||
            amountElement.ValueKind != JsonValueKind.Number ||
            !amountElement.TryGetDecimal(out var decimalAmount) ||
            !TryGetNonEmptyString(verified, "currencyCode", out var currency) ||
            !TryFromDecimalAmount(decimalAmount, currency, out var amount))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", httpStatus);
        }

        var status = ToStatus(respCode);
        var respDesc = TryGetNonEmptyString(verified, "respDesc", out var desc) ? desc : null;
        var failure = status == PaymentStatus.Failed ? ToFailure(respCode, respDesc) : null;

        DateTimeOffset? completedAt = null;
        if (status != PaymentStatus.Pending &&
            verified.TryGetProperty("transactionDateTime", out var transactionTimeElement) &&
            transactionTimeElement.ValueKind == JsonValueKind.String &&
            transactionTimeElement.GetString() is { Length: > 0 } transactionTimeRaw)
        {
            // Absent or empty is not an error (2C2P's inquiry does not always carry one); present and non-empty
            // must parse as 2C2P's own "yyyyMMddHHmmss" format, or the response is malformed.
            if (!TryParseTransactionDateTime(transactionTimeRaw, transactionTimeOffset, out var parsedTransactionTime))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", httpStatus);
            }

            completedAt = parsedTransactionTime;
        }

        return new Charge(invoiceNo, invoiceNo, amount, status, failure, completedAt);
    }
}
