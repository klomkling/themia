using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Themia.Conversations.Internal;

/// <summary>
/// Normalises a guest contact and hashes it. The format is permanent once a row is written: the HMAC input is <c>kind:value</c> (so a phone and a LINE id with the same text
/// differ), an e-mail is lower-cased, a phone must be E.164 (a national form would make one person two guests) and a LINE id keeps its case.
/// </summary>
internal static partial class GuestContactHasher
{
    private const int MaxContactLength = 254;

    public static bool TryNormalise(GuestContact? contact, out string input)
    {
        input = string.Empty;
        if (contact is null || !Enum.IsDefined(contact.Kind))
        {
            return false;
        }

        var value = contact.Value.Trim();
        var normalised = contact.Kind switch
        {
            GuestContactKind.Email => IsEmail(value) ? "email:" + value.ToLowerInvariant() : null,
            GuestContactKind.Phone => E164().IsMatch(value) ? "phone:" + value : null,
            _ => IsLineId(value) ? "line:" + value : null,
        };

        input = normalised ?? string.Empty;
        return normalised is not null;
    }

    public static byte[] Hash(byte[] pepper, string input) => HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(input));

    private static bool IsEmail(string value) =>
        value.Length <= MaxContactLength && value.IndexOf('@') is > 0 and var at && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsLineId(string value) =>
        value.Length is > 0 and <= 64 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex E164();
}
