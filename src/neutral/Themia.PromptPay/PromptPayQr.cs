using System.Globalization;
using Themia.PromptPay.Internal;

namespace Themia.PromptPay;

/// <summary>
/// Builds PromptPay QR payloads. Pure computation — no HTTP, no credentials, no clock, no I/O.
/// </summary>
/// <remarks>
/// The result is the payload <em>string</em>. Turning it into a QR image is the application's choice and
/// deliberately not included: image rendering pulls a drawing dependency into every consumer, and the
/// payload is the part that has to be right.
/// <para>
/// <b>Out of scope, permanently:</b> invoices, reconciliation, running numbers, withholding tax, and the
/// decision of what a reference should contain. This package accepts a reference and a registration and
/// constructs a correct payload; it does not decide what they mean.
/// </para>
/// </remarks>
public static class PromptPayQr
{
    /// <summary>The Application Identifier for Credit Transfer (Tag 29).</summary>
    public const string CreditTransferAid = "A000000677010111";

    /// <summary>The Application Identifier for Bill Payment (Tag 30).</summary>
    public const string BillPaymentAid = "A000000677010112";

    internal const int BillPaymentAidLength = 16;

    private const string PayloadFormatIndicator = "01";
    private const string StaticQr = "11";
    private const string DynamicQr = "12";
    private const string ThaiBaht = "764";
    private const string Thailand = "TH";

    /// <summary>
    /// Builds a Credit Transfer (Tag 29) payload paying the given proxy.
    /// </summary>
    /// <remarks>
    /// Tag 29 carries no reference fields — see <see cref="PromptPayProxy"/> for why that limits what a
    /// payment made this way can be matched to.
    /// </remarks>
    /// <param name="proxy">The recipient.</param>
    /// <param name="amount">A fixed amount, or <see langword="null"/> to let the payer enter one.</param>
    /// <returns>The QR payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is zero, negative, or too long to encode.</exception>
    public static string CreditTransfer(PromptPayProxy proxy, decimal? amount = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        var merchantAccount = EmvQrPayload.Concat(
            EmvQrPayload.Tag("00", CreditTransferAid),
            EmvQrPayload.Tag(proxy.SubTagId, proxy.Value));

        return Complete(EmvQrPayload.Tag("29", merchantAccount), amount, extra: null);
    }

    /// <summary>
    /// Builds a Bill Payment (Tag 30) payload against a biller registration.
    /// </summary>
    /// <remarks>
    /// The product a payment belongs to comes from <paramref name="biller"/>, not from this call — see
    /// <see cref="BillerRegistration"/>. When the registration carries a
    /// <see cref="BillerRegistration.ProductPrefix"/> it is prepended to <paramref name="reference"/>
    /// here, so a call site never does prefix arithmetic and never omits it.
    /// </remarks>
    /// <param name="biller">The biller registration, carrying the biller id and the product discriminator.</param>
    /// <param name="reference">Reference 1 — your own running number. Any product prefix is added for you.</param>
    /// <param name="amount">A fixed amount, or <see langword="null"/> to let the payer enter one.</param>
    /// <param name="reference2">Reference 2, when the biller's agreement defines one. Shortens the room left for <paramref name="reference"/>.</param>
    /// <returns>The QR payload.</returns>
    /// <exception cref="ArgumentException">A reference is empty, not printable ASCII, or too long to encode.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is zero, negative, or too long to encode.</exception>
    public static string BillPayment(
        BillerRegistration biller, string reference, decimal? amount = null, string? reference2 = null)
    {
        ArgumentNullException.ThrowIfNull(biller);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        BillerRegistration.RequirePrintableAscii(reference, nameof(reference));

        if (reference2 is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference2);
            BillerRegistration.RequirePrintableAscii(reference2, nameof(reference2));
        }

        // Checked against the exact budget rather than the headline number, because Reference 2 spends
        // from the same 99 characters. MaxReferenceLength has already reserved the product prefix, so the
        // comparison is against the reference the caller passed, not the composed one. Rejecting here
        // beats emitting a payload a bank refuses later, when the only evidence is a customer saying the
        // QR did not work.
        var available = biller.MaxReferenceLength - (reference2 is null ? 0 : 4 + reference2.Length);
        if (reference.Length > available)
        {
            throw new ArgumentException(
                $"Reference is {reference.Length} characters; at most {available} fit"
                + (biller.ProductPrefix is null
                    ? string.Empty
                    : $" once the '{biller.ProductPrefix}' product prefix has been reserved")
                + (reference2 is null
                    ? " for this biller id."
                    : $" and Reference 2 ('{reference2}') has taken its share of the same limit."),
                nameof(reference));
        }

        var composed = biller.ComposeReference(reference);

        var merchantAccount = EmvQrPayload.Concat(
            EmvQrPayload.Tag("00", BillPaymentAid),
            EmvQrPayload.Tag("01", biller.BillerId),
            EmvQrPayload.Tag("02", composed),
            reference2 is null ? string.Empty : EmvQrPayload.Tag("03", reference2));

        return Complete(EmvQrPayload.Tag("30", merchantAccount), amount, extra: null);
    }

    private static string Complete(string merchantAccountTag, decimal? amount, string? extra)
    {
        // Tag order is deliberate and pinned by the golden vectors: currency, country, then amount. EMVCo
        // requires only that tag 00 comes first and the checksum last, but the checksum covers the whole
        // string, so any reordering changes every expected payload in the test suite.
        var payload = EmvQrPayload.Concat(
            EmvQrPayload.Tag("00", PayloadFormatIndicator),
            EmvQrPayload.Tag("01", amount is null ? StaticQr : DynamicQr),
            merchantAccountTag,
            EmvQrPayload.Tag("53", ThaiBaht),
            EmvQrPayload.Tag("58", Thailand),
            amount is null ? string.Empty : EmvQrPayload.Tag("54", FormatAmount(amount.Value)),
            extra ?? string.Empty);

        return EmvQrPayload.WithChecksum(payload);
    }

    private static string FormatAmount(decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        // Always two decimals: "30" renders as "30.00", matching what every bank app displays and what
        // the golden vectors pin.
        var formatted = amount.ToString("0.00", CultureInfo.InvariantCulture);

        if (formatted.Length > 13)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "Encodes to more than the 13 characters EMVCo allows for an amount.");
        }

        return formatted;
    }
}
