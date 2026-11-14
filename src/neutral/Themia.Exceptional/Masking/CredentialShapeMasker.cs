using System.Text.RegularExpressions;

namespace Themia.Exceptional.Masking;

/// <summary>Masks credential-shaped values (bearer/basic, Authorization, key=value secrets, JWTs) in free text.</summary>
internal static class CredentialShapeMasker
{
    private const string Mask_ = "***";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Patterns =
    [
        new(@"\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", Options, MatchTimeout),
        // Authorization header: whatever the scheme, mask the whole value; an unclosed quote masks to the end.
        new(@"[""']?\bauthorization\b[""']?\s*[=:]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[""'][\s\S]*|[^\r\n]*)", Options, MatchTimeout),
        // Keys matched by suffix (access_token, client_secret, db_password, ...).
        new(@"[""']?\b(?:[\w.-]*(?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|credentials?)|auth)[""']?\s*[=:]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[""'][\s\S]*|\S+)", Options, MatchTimeout),
        // JWT-like: three base64url segments starting with the "eyJ" header.
        new(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
    ];

    static CredentialShapeMasker() => RegexWarmUp.Run(Patterns);

    /// <summary>Masks <paramref name="value"/>; a value that defeats the masker (match timeout) becomes <c>***</c> as a whole.</summary>
    public static string? Mask(string? value) => Mask(value, Patterns);

    internal static string? Mask(string? value, Regex[] patterns)
    {
        if (value is null)
            return null;

        foreach (var pattern in patterns)
        {
            try
            {
                value = pattern.Replace(value, Mask_);
            }
            catch (RegexMatchTimeoutException)
            {
                return Mask_;
            }
        }
        return value;
    }
}
