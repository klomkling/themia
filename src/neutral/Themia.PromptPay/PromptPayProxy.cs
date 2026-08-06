namespace Themia.PromptPay;

/// <summary>
/// The recipient of a Credit Transfer (Tag 29) QR — a mobile number, a National/Tax ID, or an e-wallet id.
/// </summary>
/// <remarks>
/// Tag 29 carries <b>no reference fields</b>. A payment made against one of these arrives with nothing
/// identifying what it was for, so reconciliation falls back to amount and timestamp — which stops
/// working the moment two payers owe the same amount in the same window. Use
/// <see cref="PromptPayQr.BillPayment"/> when payments have to be matched to anything.
/// </remarks>
public sealed class PromptPayProxy
{
    private PromptPayProxy(string subTagId, string value)
    {
        SubTagId = subTagId;
        Value = value;
    }

    internal string SubTagId { get; }

    internal string Value { get; }

    /// <summary>
    /// A Thai mobile number, sent as the 13-character form Tag 29 requires
    /// (<c>0066</c> + the number without its leading zero).
    /// </summary>
    /// <remarks>
    /// Formatting characters — spaces, hyphens, parentheses, a leading <c>+</c> — are stripped. Nothing
    /// else is inferred: the digits that remain must be either the 10-digit national form starting
    /// <c>0</c> or the 11-digit form starting <c>66</c>. A number in some other country's national format
    /// is rejected rather than reinterpreted as Thai, because guessing there means sending money to a
    /// stranger who happens to hold the resulting number.
    /// </remarks>
    /// <param name="mobileNumber">The mobile number, with or without formatting.</param>
    /// <returns>The proxy.</returns>
    /// <exception cref="ArgumentException">The number is not a recognisable Thai mobile number.</exception>
    public static PromptPayProxy MobileNumber(string mobileNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mobileNumber);

        var digits = StripFormatting(mobileNumber);
        var national = digits switch
        {
            { Length: 10 } when digits[0] == '0' => digits[1..],
            { Length: 11 } when digits.StartsWith("66", StringComparison.Ordinal) => digits[2..],
            _ => throw new ArgumentException(
                "Must be a Thai mobile number in either the 10-digit national form (0XXXXXXXXX) or the "
                + "11-digit form starting 66. Formatting characters are stripped; nothing else is inferred.",
                nameof(mobileNumber)),
        };

        RequireDigitsOnly(national, nameof(mobileNumber));

        // Tag 29 sub-tag 01 is a fixed 13 characters: the country code with the number zero-padded on the
        // left. "0812223333" becomes "0066812223333".
        return new PromptPayProxy("01", "0066" + national.PadLeft(9, '0'));
    }

    /// <summary>A 13-digit Thai National ID or Tax ID.</summary>
    /// <param name="nationalOrTaxId">The 13-digit identifier.</param>
    /// <returns>The proxy.</returns>
    /// <exception cref="ArgumentException">The value is not 13 digits.</exception>
    public static PromptPayProxy NationalId(string nationalOrTaxId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nationalOrTaxId);

        var digits = StripFormatting(nationalOrTaxId);
        if (digits.Length != 13)
        {
            throw new ArgumentException("Must be exactly 13 digits.", nameof(nationalOrTaxId));
        }

        RequireDigitsOnly(digits, nameof(nationalOrTaxId));
        return new PromptPayProxy("02", digits);
    }

    /// <summary>A 15-digit e-wallet id.</summary>
    /// <param name="eWalletId">The 15-digit identifier.</param>
    /// <returns>The proxy.</returns>
    /// <exception cref="ArgumentException">The value is not 15 digits.</exception>
    public static PromptPayProxy EWalletId(string eWalletId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eWalletId);

        var digits = StripFormatting(eWalletId);
        if (digits.Length != 15)
        {
            throw new ArgumentException("Must be exactly 15 digits.", nameof(eWalletId));
        }

        RequireDigitsOnly(digits, nameof(eWalletId));
        return new PromptPayProxy("03", digits);
    }

    private static string StripFormatting(string value)
    {
        var kept = new char[value.Length];
        var count = 0;

        foreach (var character in value)
        {
            if (character is ' ' or '-' or '(' or ')' or '+' or '.')
            {
                continue;
            }

            kept[count++] = character;
        }

        return new string(kept, 0, count);
    }

    private static void RequireDigitsOnly(string value, string parameterName)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                throw new ArgumentException("Must contain digits only once formatting is stripped.", parameterName);
            }
        }
    }
}
