using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Themia.Messaging.Hmac;

/// <summary>
/// The <c>themia-hmac-v1</c> signing scheme. Deliberately fixed and not configurable: canonicalization is
/// where signature-bypass bugs live, and an adopter-swappable canonical string would let two services
/// running the same framework fail to talk to each other.
/// </summary>
public static class ThemiaHmacV1
{
    /// <summary>The scheme identifier carried in the scheme header.</summary>
    public const string SchemeName = "themia-hmac-v1";

    /// <summary>The timestamp format: ISO-8601 round-trip, seven fractional digits, trailing <c>Z</c>.</summary>
    private const string TimestampFormat = "O";

    /// <summary>
    /// Builds the canonical string that is signed: <c>{timestamp}\n{METHOD}\n{pathAndQuery}\n{body}</c>.
    /// </summary>
    /// <remarks>
    /// An empty body contributes the empty string and its separator newline is RETAINED — the segment is
    /// never omitted. Both ends CONSTRUCT this string rather than parsing it, so a newline inside the body
    /// cannot shift a field boundary.
    /// </remarks>
    /// <param name="timestamp">The timestamp string, byte-identical to the one sent in the header.</param>
    /// <param name="method">The HTTP method; upper-cased here.</param>
    /// <param name="pathAndQuery">The path and query exactly as sent — not re-encoded, decoded or reordered.</param>
    /// <param name="body">The raw body string; empty for a bodyless request.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    public static string Canonicalize(string timestamp, string method, string pathAndQuery, string body)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(body);

        return string.Join('\n', timestamp, method.ToUpperInvariant(), pathAndQuery, body);
    }

    /// <summary>Computes the lowercase-hex HMAC-SHA256 of <paramref name="canonical"/>.</summary>
    /// <remarks>The secret is used as RAW UTF-8 string bytes — never hex- or base64-decoded.</remarks>
    /// <param name="canonical">The canonical string from <see cref="Canonicalize"/>.</param>
    /// <param name="secret">The shared secret, used as UTF-8 bytes.</param>
    /// <returns>The signature as lowercase hexadecimal.</returns>
    public static string Sign(string canonical, string secret)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Formats an instant in the scheme's timestamp format.</summary>
    /// <remarks>
    /// Formats via <see cref="DateTimeOffset.UtcDateTime"/> (a <c>DateTime</c> with <c>Kind=Utc</c>) rather
    /// than the <see cref="DateTimeOffset"/> itself: <see cref="DateTimeOffset"/>'s <c>"O"</c> format renders
    /// the zero offset as <c>+00:00</c>, while a UTC-kind <c>DateTime</c> renders it as the required trailing
    /// <c>Z</c>.
    /// </remarks>
    /// <param name="value">The instant to format; converted to UTC.</param>
    /// <returns>The timestamp string for the header and the canonical string.</returns>
    public static string FormatTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses a timestamp in the scheme's format.</summary>
    /// <remarks>
    /// <see cref="DateTimeStyles.AssumeUniversal"/> (with <see cref="DateTimeStyles.AdjustToUniversal"/>)
    /// is combined with <see cref="DateTimeStyles.RoundtripKind"/> so a value with NO offset designator is
    /// treated as UTC rather than server-local time. These services run on UTC+7 hosts: a naive timestamp
    /// read in local time would land ~7 hours off, fail the clock-skew window, and produce a permanent 408
    /// loop that looks exactly like clock drift — in a transport whose entire design is about
    /// distinguishing clock problems from credential problems. This changes nothing for offset-bearing
    /// values (every golden vector carries a trailing <c>Z</c>) and fails safe for naive ones.
    /// </remarks>
    /// <param name="value">The header value.</param>
    /// <param name="result">The parsed instant when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed timestamp.</returns>
    public static bool TryParseTimestamp(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
}
