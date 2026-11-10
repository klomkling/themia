using System.Text.RegularExpressions;

namespace Themia.Exceptional.Alerts;

/// <summary>Always-on masking of exception text before it leaves the application for a third-party channel.</summary>
internal static class AlertMessageMasker
{
    private const string Mask_ = "***";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Patterns =
    [
        // Fail closed: a closed quoted value (escapes honoured) is masked as a unit; an unclosed quote masks to the end
        // of the message; an unquoted value masks to the next whitespace.
        // Keys are matched by suffix (access_token, client_secret, db_password, x-api-key, ...), case-insensitively.
        // Credentials written as key=value / key: value, and Authorization-style bearer/basic values.
        new(@"\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", Options, MatchTimeout),
        // Authorization header: whatever the scheme (Bearer, Basic, Digest, ...), mask the whole value: a quoted value as a unit, an unclosed quote to the end of the message, otherwise to the end of the line.
        new(@"[""']?\bauthorization\b[""']?\s*[=:]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[""'][\s\S]*|[^\r\n]*)", Options, MatchTimeout),
        new(@"[""']?\b(?:[\w.-]*(?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|credentials?)|auth)[""']?\s*[=:]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[""'][\s\S]*|\S+)", Options, MatchTimeout),
        // JWT-like: three base64url segments starting with the "eyJ" header.
        new(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
        // Standard-alphabet base64 blobs (may contain +, / and = padding).
        new(@"[A-Za-z0-9+/]{32,}={0,2}", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
        // Opaque tokens / keys: 32+ url-safe characters in a row.
        new(@"[A-Za-z0-9_-]{32,}", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
        // Phone, national id, card numbers: 8+ digits, optionally separated by single spaces or dashes.
        new(@"\d(?:[ -]?\d){7,}", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
    ];

    public static string Mask(string value)
    {
        foreach (var pattern in Patterns)
        {
            try
            {
                value = pattern.Replace(value, Mask_);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail closed: text that defeats the masker is not sent at all.
                return Mask_;
            }
        }
        return value;
    }
}
