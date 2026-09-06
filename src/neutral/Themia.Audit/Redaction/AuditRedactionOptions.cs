namespace Themia.Audit.Redaction;

/// <summary>
/// Configures which JSON property names <see cref="AuditRedactor"/> treats as sensitive. Adopters can
/// only <em>add</em> patterns, never remove the defaults — an adopter who needs a default gone supplies
/// their own <see cref="IAuditRedactor"/>, a conspicuous act rather than a config line.
/// </summary>
public sealed class AuditRedactionOptions
{
    // Matched case-insensitively against JSON property names at any depth.
    private static readonly string[] DefaultPatterns =
    [
        "password", "passwordhash", "passwordsalt", "secret", "token", "refreshtoken", "accesstoken",
        "apikey", "api_key", "authorization", "otp", "pin", "cvv", "creditcard", "card_number",
        "privatekey", "ssn",
    ];

    private readonly HashSet<string> patterns = new(DefaultPatterns, StringComparer.OrdinalIgnoreCase);

    /// <summary>The property-name patterns currently treated as sensitive, matched case-insensitively.</summary>
    public IReadOnlyCollection<string> Patterns => patterns;

    /// <summary>Adds a property-name pattern to redact, in addition to the defaults.</summary>
    /// <param name="pattern">The property name to treat as sensitive. Matched case-insensitively.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is null, empty, or whitespace.</exception>
    public void AddPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        patterns.Add(pattern);
    }

    /// <summary>Returns whether <paramref name="propertyName"/> matches a configured pattern.</summary>
    /// <param name="propertyName">A JSON property name.</param>
    internal bool Matches(string propertyName) => patterns.Contains(propertyName);
}
