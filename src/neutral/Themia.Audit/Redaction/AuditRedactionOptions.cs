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

    /// <summary>Adds a property-name pattern to redact, in addition to the defaults.</summary>
    /// <param name="pattern">The property name to treat as sensitive. Matched case-insensitively.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is null, empty, or whitespace.</exception>
    public void AddPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        patterns.Add(pattern);
    }

    /// <summary>
    /// The supported way to test whether a JSON property name is treated as sensitive.
    /// </summary>
    /// <remarks>
    /// Matching is case-insensitive: <c>"PASSWORD"</c>, <c>"Password"</c> and <c>"password"</c> all
    /// match. The default deny-list cannot be removed — only added to via <see cref="AddPattern"/> — so
    /// this always returns <see langword="true"/> for a default pattern regardless of what else has been
    /// registered.
    /// </remarks>
    /// <param name="propertyName">A JSON property name.</param>
    /// <returns><see langword="true"/> if <paramref name="propertyName"/> matches a configured pattern.</returns>
    public bool IsSensitive(string propertyName) => patterns.Contains(propertyName);
}
