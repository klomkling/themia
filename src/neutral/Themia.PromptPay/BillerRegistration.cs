using Themia.PromptPay.Internal;

namespace Themia.PromptPay;

/// <summary>
/// A biller's PromptPay registration, and the product a payment made against it belongs to.
/// </summary>
/// <remarks>
/// <b>The product discriminator lives here, not on the call.</b> Two products billing under one Tax ID
/// must be distinguishable, and where they are distinguished depends on what the bank issued:
/// <list type="bullet">
/// <item><description>
/// Two suffixes under one Tax ID — the suffix <em>is</em> the discriminator.
/// Use <see cref="PerProductSuffix"/>.
/// </description></item>
/// <item><description>
/// One suffix for both — the discriminator has to move into the reference, as a prefix.
/// Use <see cref="SharedSuffix"/>.
/// </description></item>
/// </list>
/// <para>
/// An earlier design took the biller id and suffix as separate required inputs and called that the fix
/// for the collision. It was not: it protects only the first case. In the second, both products pass the
/// same suffix and the discriminator becomes a free-text prefix that nothing validates — the original
/// silent failure, with a call site that now looks guarded. Where both products' payments land in one
/// receiving account, that string is the only thing distinguishing them: not a formatting convention
/// with a safety net behind it, but the safety net. Hence a registration that cannot be constructed
/// without stating the product either way, and that owns the concatenation so the prefix and the
/// reference are length-checked together.
/// </para>
/// <para>
/// Switching between the two when the bank answers is one line at the composition root, not a convention
/// change audited across every call site.
/// </para>
/// </remarks>
public sealed class BillerRegistration
{
    /// <summary>The length of a Thai Tax ID (also the length of a National ID).</summary>
    public const int TaxIdLength = 13;

    /// <summary>The length of the biller suffix that follows the Tax ID.</summary>
    public const int SuffixLength = 2;

    // Tag 30's value must fit 99 characters, and it always carries the AID and the biller id:
    //   "00" + "16" + 16-character AID                     = 20
    //   "01" + length + biller id (TaxIdLength + Suffix)   = 4 + 15
    // leaving 99 - 20 - 19 = 60 for Ref1 and Ref2 together, each costing 4 characters of header.
    private const int AidTagCost = 4 + PromptPayQr.BillPaymentAidLength;

    private BillerRegistration(string billerId, string? productPrefix, int? configuredMaxReferenceLength)
    {
        BillerId = billerId;
        ProductPrefix = productPrefix;

        var structural = EmvQrPayload.MaxTagValueLength
            - AidTagCost
            - (4 + billerId.Length)
            - 4
            - (productPrefix?.Length ?? 0);

        if (configuredMaxReferenceLength is { } configured)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configured, nameof(configuredMaxReferenceLength));

            if (configured > structural)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(configuredMaxReferenceLength),
                    configured,
                    $"Cannot exceed the {structural} characters the EMVCo format leaves for the reference with this "
                    + "biller id and product prefix. This value may only tighten the structural limit, never widen it.");
            }
        }

        MaxReferenceLength = configuredMaxReferenceLength ?? structural;
    }

    /// <summary>The 15-digit Biller ID sent in Tag 30: the Tax ID followed by the 2-digit suffix.</summary>
    public string BillerId { get; }

    /// <summary>
    /// The product prefix prepended to every reference, or <see langword="null"/> when the suffix already
    /// distinguishes the product.
    /// </summary>
    public string? ProductPrefix { get; }

    /// <summary>
    /// The longest reference <see cref="PromptPayQr.BillPayment"/> accepts for this registration, with
    /// <see cref="ProductPrefix"/> already subtracted.
    /// </summary>
    /// <remarks>
    /// Derived from the EMVCo format rather than from a bank's rule: Tag 30's value is capped at 99
    /// characters and always spends 20 on the AID and 19 on a 15-digit biller id, which leaves 56 for a
    /// reference with no Reference 2. <b>Supplying a Reference 2 lowers it further</b> — that costs four
    /// characters plus its own length, and <see cref="PromptPayQr.BillPayment"/> checks the exact total.
    /// <para>
    /// A bank's own limit may be lower and is not knowable from here, so nothing in this package invents
    /// one. Pass <c>maxReferenceLength</c> to tighten this to the number your bank gives you.
    /// </para>
    /// </remarks>
    public int MaxReferenceLength { get; }

    /// <summary>
    /// A registration whose 2-digit suffix identifies the product, because the bank issued one suffix per
    /// product under the same Tax ID.
    /// </summary>
    /// <param name="taxId">The 13-digit Tax ID the biller registered under.</param>
    /// <param name="suffix">The 2-digit suffix identifying this product.</param>
    /// <param name="maxReferenceLength">Tightens <see cref="MaxReferenceLength"/> to the bank's own limit, when known.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentException"><paramref name="taxId"/> or <paramref name="suffix"/> is not the required number of digits.</exception>
    public static BillerRegistration PerProductSuffix(string taxId, string suffix, int? maxReferenceLength = null)
    {
        RequireDigits(taxId, TaxIdLength, nameof(taxId));
        RequireDigits(suffix, SuffixLength, nameof(suffix));
        return new BillerRegistration(taxId + suffix, productPrefix: null, maxReferenceLength);
    }

    /// <summary>
    /// A registration whose suffix is shared with another product, so the product is identified by a
    /// prefix this package prepends to every reference.
    /// </summary>
    /// <remarks>
    /// The prefix is required and has no default. With a shared suffix and a shared receiving account it
    /// is the only signal attributing a payment to a product — not the destination, not the amount, not
    /// the timing — so a caller must not be able to reach a payload without having stated it.
    /// </remarks>
    /// <param name="taxId">The 13-digit Tax ID the biller registered under.</param>
    /// <param name="suffix">The 2-digit suffix, shared with the other product.</param>
    /// <param name="productPrefix">Prepended to every reference, e.g. <c>"EA-"</c>. Written verbatim, including any separator.</param>
    /// <param name="maxReferenceLength">Tightens <see cref="MaxReferenceLength"/> to the bank's own limit, when known.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentException">A value is not the required number of digits, or the prefix is empty or not printable ASCII.</exception>
    public static BillerRegistration SharedSuffix(string taxId, string suffix, string productPrefix, int? maxReferenceLength = null)
    {
        RequireDigits(taxId, TaxIdLength, nameof(taxId));
        RequireDigits(suffix, SuffixLength, nameof(suffix));
        ArgumentException.ThrowIfNullOrWhiteSpace(productPrefix);
        RequirePrintableAscii(productPrefix, nameof(productPrefix));

        return new BillerRegistration(taxId + suffix, productPrefix, maxReferenceLength);
    }

    /// <summary>Applies <see cref="ProductPrefix"/> to a caller's reference.</summary>
    internal string ComposeReference(string reference) =>
        ProductPrefix is null ? reference : ProductPrefix + reference;

    internal static void RequirePrintableAscii(string value, string parameterName)
    {
        foreach (var character in value)
        {
            // The EMVCo payload is ASCII and the checksum is computed over it byte by byte. Anything
            // outside this range would either be rejected downstream or checksum over the wrong bytes.
            if (character is < ' ' or > '~')
            {
                throw new ArgumentException(
                    $"Contains the character U+{(int)character:X4}; only printable ASCII is permitted in a PromptPay payload.",
                    parameterName);
            }
        }
    }

    private static void RequireDigits(string value, int length, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length != length)
        {
            throw new ArgumentException($"Must be exactly {length} digits (was {value.Length}).", parameterName);
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                throw new ArgumentException("Must contain digits only.", parameterName);
            }
        }
    }
}
