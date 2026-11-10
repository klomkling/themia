using System.Text.RegularExpressions;
using Themia.Exceptional.Masking;

namespace Themia.Exceptional.Alerts;

/// <summary>
/// Always-on masking of exception text before it leaves the application for a third-party channel: the shared
/// credential-shape masking, then the patterns that only matter off-box (email, opaque tokens, long digit runs).
/// </summary>
internal static class AlertMessageMasker
{
    private const string Mask_ = "***";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex[] Patterns =
    [
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
        value = CredentialShapeMasker.Mask(value)!;
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
