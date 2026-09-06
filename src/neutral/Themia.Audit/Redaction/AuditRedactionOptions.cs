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
    /// <remarks>
    /// For enumeration and diagnostics only — do not test membership with <c>Patterns.Contains(...)</c>.
    /// This returns a snapshot array, not the backing set, so that call binds to LINQ's
    /// <see cref="Enumerable.Contains{TSource}(IEnumerable{TSource}, TSource)"/> against a plain
    /// <see cref="Array"/>, which compares ordinally and ignores the case-insensitive comparer this
    /// type matches with internally. Use <see cref="IsSensitive"/> instead, which is guaranteed to
    /// match the same way <see cref="AuditRedactor"/> does — exposing the live set here would let
    /// <c>Contains</c> appear to work only because <see cref="HashSet{T}"/> happens to special-case
    /// <see cref="System.Collections.Generic.ICollection{T}.Contains(T)"/> through its own comparer, an
    /// accident of the backing type that <see cref="IsSensitive"/> does not depend on.
    /// </remarks>
    public IReadOnlyCollection<string> Patterns => patterns.ToArray();

    /// <summary>Adds a property-name pattern to redact, in addition to the defaults.</summary>
    /// <param name="pattern">The property name to treat as sensitive. Matched case-insensitively.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is null, empty, or whitespace.</exception>
    public void AddPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        patterns.Add(pattern);
    }

    /// <summary>
    /// The supported way to test whether a JSON property name is treated as sensitive — matched the same
    /// way <see cref="AuditRedactor"/> matches it: case-insensitively, regardless of how the pattern was
    /// registered.
    /// </summary>
    /// <param name="propertyName">A JSON property name.</param>
    /// <returns><see langword="true"/> if <paramref name="propertyName"/> matches a configured pattern.</returns>
    public bool IsSensitive(string propertyName) => patterns.Contains(propertyName);
}
