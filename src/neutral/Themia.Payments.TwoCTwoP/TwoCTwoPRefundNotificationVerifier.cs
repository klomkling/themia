using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.TwoCTwoP;

/// <summary>
/// Opens the asynchronous refund notification 2C2P POSTs to <see cref="TwoCTwoPOptions.RefundNotifyUrl"/>: a form body <c>notifyResponse=&lt;token&gt;</c> whose token is a JWS (<c>PS256</c>, signed by 2C2P) around
/// a JWE (<c>RSA-OAEP</c> + <c>A256GCM</c>, encrypted to the merchant key). The signature is checked with the 2C2P certificate BEFORE anything is decrypted with the merchant key, and the algorithms are pinned.
/// <para>
/// <b>Register it as itself, not as <see cref="IPaymentWebhookVerifier"/>:</b> that slot is the payment webhook's, and a second registration would replace it. Map the notify endpoint to this class and set the endpoint's
/// request body limit to 64 KiB (the same cap <see cref="Verify"/> applies after the body has been read).
/// </para>
/// <para>
/// <b>What to do with the event: treat a <see cref="WebhookOutcome.Verified"/> refund event as a nudge to call <see cref="Themia.Payments.IRefundStatusGateway.GetRefundsAsync"/> for the charge and reconcile by
/// <see cref="RefundRecord.IdempotencyKey"/>, as the poll already does. Never write the event's status onto a refund row:</b> the notification does not say which of several refunds of one invoice it is about,
/// and <see cref="TwoCTwoPPaymentGateway.RefundAsync"/> never learns 2C2P's reference. A body is authentic, not fresh (nothing in it expires), so a captured one replays for ever; as a nudge a replay costs one extra read.
/// Coalesce nudges per charge if you want to bound that.
/// </para>
/// <para>
/// The event's status uses the same mapping as the poll (<c>RF</c> succeeded; <c>RFF</c>, <c>RR</c>, <c>RR1</c>-<c>RR3</c> failed; anything else, or a <c>respCode</c> other than <c>00</c>, is
/// <see cref="PaymentEventType.Other"/> and pending, never succeeded). <see cref="WebhookOutcome.SignatureMismatch"/> also covers a valid signature whose content cannot be decrypted (for example after the merchant key was
/// rotated), so read it as "check the keys" before "attack". When the refund keys are missing or invalid the call throws <see cref="InvalidOperationException"/>: that is a misconfiguration, not a verdict on the sender.
/// Not verified against the 2C2P sandbox: the field names, the form encoding and which reference the notification carries.
/// </para>
/// </summary>
public sealed class TwoCTwoPRefundNotificationVerifier : IPaymentWebhookVerifier, IDisposable
{
    private const int MaxBodyBytes = 64 * 1024;
    private const string FieldName = "notifyResponse";
    private const string AcceptedRespCode = "00";
    private const string TimeStampFormat = "yyyyMMddHHmmss";

    private readonly IOptions<TwoCTwoPOptions> options;
    private readonly object keysLock = new();
    private TwoCTwoPRefundKeys? keys;

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">The 2C2P options; the refund keys are read from them on first use and kept.</param>
    public TwoCTwoPRefundNotificationVerifier(IOptions<TwoCTwoPOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The refund keys are not configured or are invalid.</exception>
    public WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (rawBody.IsEmpty || rawBody.Length > MaxBodyBytes || !TryReadToken(rawBody, out var token))
        {
            return Malformed();
        }

        var refundKeys = Keys();
        if (!JweJws.TryOpen(token, refundKeys.ProviderPublic, refundKeys.MerchantPrivate, out var xml))
        {
            return new WebhookVerification(WebhookOutcome.SignatureMismatch, null);
        }

        var settings = options.Value;
        var notification = TwoCTwoPRefundXml.ParseNotification(xml);
        if (notification is null
            || (notification.MerchantId is not null && notification.MerchantId != settings.MerchantId)
            || (notification.ProcessType is not null && notification.ProcessType != "R")
            || !TryParseTimeStamp(notification.TimeStamp, settings.TransactionTimeOffset, out var occurredAt))
        {
            return Malformed();
        }

        // respCode first: anything but 00 says nothing reliable about the refund, whatever status rides along.
        var status = notification.RespCode == AcceptedRespCode ? TwoCTwoPPaymentGateway.MapRefundStatus(notification.Status) : PaymentStatus.Pending;
        var type = status switch
        {
            PaymentStatus.Succeeded => PaymentEventType.RefundSucceeded,
            PaymentStatus.Failed => PaymentEventType.RefundFailed,
            _ => PaymentEventType.Other,
        };
        var paymentEvent = new PaymentEvent(type, notification.InvoiceNo, notification.InvoiceNo, null, status, occurredAt, ToJson(notification));
        return new WebhookVerification(WebhookOutcome.Verified, paymentEvent);
    }

    /// <summary>Releases the cached keys.</summary>
    public void Dispose()
    {
        lock (keysLock)
        {
            keys?.Dispose();
            keys = null;
        }
    }

    private static WebhookVerification Malformed() => new(WebhookOutcome.Malformed, null);

    /// <summary>The one non-empty <c>notifyResponse</c> field of an <c>application/x-www-form-urlencoded</c> body; other fields are unsigned and ignored.</summary>
    private static bool TryReadToken(ReadOnlySpan<byte> rawBody, out string token)
    {
        token = "";
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(rawBody);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string? found = null;
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Decode(separator < 0 ? pair : pair[..separator]);
            if (name != FieldName)
            {
                continue;
            }

            if (found is not null || separator < 0)
            {
                return false; // duplicated, or no value
            }

            found = Decode(pair[(separator + 1)..]).Trim();
        }

        if (string.IsNullOrEmpty(found))
        {
            return false;
        }

        token = found;
        return true;
    }

    /// <summary>x-www-form-urlencoded decoding: <c>+</c> is a space, then percent-decoding.</summary>
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>2C2P's <c>timeStamp</c> is <c>yyMMddHHmmss</c> with no zone; the century is always 20xx.</summary>
    private static bool TryParseTimeStamp(string? value, TimeSpan offset, out DateTimeOffset occurredAt)
    {
        occurredAt = default;
        if (value is not { Length: 12 } || !value.All(char.IsAsciiDigit)
            || !DateTime.TryParseExact("20" + value, TimeStampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        occurredAt = new DateTimeOffset(local, offset);
        return true;
    }

    /// <summary>The verified fields the app may need, without the card or the payer (no masked PAN, scheme or ECI).</summary>
    private static string ToJson(TwoCTwoPRefundXml.Notification notification)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            Write(writer, "respCode", notification.RespCode);
            Write(writer, "invoiceNo", notification.InvoiceNo);
            Write(writer, "status", notification.Status);
            Write(writer, "amount", notification.Amount);
            Write(writer, "referenceNo", notification.ReferenceNo);
            Write(writer, "refundReferenceNo", notification.RefundReferenceNo);
            Write(writer, "approvalCode", notification.ApprovalCode);
            Write(writer, "idempotencyID", notification.IdempotencyId);
            Write(writer, "userDefined1", notification.UserDefined1);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private TwoCTwoPRefundKeys Keys()
    {
        lock (keysLock)
        {
            if (keys is not null)
            {
                return keys;
            }

            var settings = options.Value;
            if (!TwoCTwoPRefundKeys.IsConfigured(settings))
            {
                throw new InvalidOperationException("Refund notifications need TwoCTwoPOptions.MerchantPrivateKeyPem and ProviderPublicCertificatePem.");
            }

            // A failure is not cached: the next call tries again.
            return TwoCTwoPRefundKeys.TryCreate(settings, TimeProvider.System, out var created, out var error)
                ? keys = created!
                : throw new InvalidOperationException(error);
        }
    }
}
