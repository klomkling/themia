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
    private readonly List<string[]> exemptions = [];

    /// <summary>Adds a property-name pattern to redact, in addition to the defaults.</summary>
    /// <param name="pattern">The property name to treat as sensitive. Matched case-insensitively.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is null, empty, or whitespace.</exception>
    public void AddPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        patterns.Add(pattern);
    }

    /// <summary>
    /// Adds an exact JSON path exemption (e.g. <c>"$.after.mobileVerified"</c> or <c>"$.tokenCount"</c>)
    /// that is exempt from redaction even if its property name matches a sensitive pattern.
    /// </summary>
    /// <remarks>
    /// Array indices add no path segment, so an exemption such as <c>"$.items.pin"</c> exempts <c>pin</c>
    /// across every element of the <c>items</c> array.
    /// </remarks>
    /// <param name="jsonPath">The exact JSON path. Must start with <c>"$."</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="jsonPath"/> is null, whitespace, or does not start with <c>"$."</c>.</exception>
    public void AddExemption(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        if (!jsonPath.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Exemption path '{jsonPath}' must start with '$.' (e.g. '$.after.mobileVerified'). " +
                "Exemptions are matched by exact JSON path only to prevent un-redacting sensitive fields elsewhere.",
                nameof(jsonPath));
        }

        var segments = jsonPath[2..].Split('.', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"Exemption path '{jsonPath}' contains empty segments.",
                nameof(jsonPath));
        }

        exemptions.Add(segments);
    }

    /// <summary>
    /// Checks whether an exact JSON path is exempt from redaction.
    /// </summary>
    /// <param name="jsonPath">The full JSON path starting with <c>"$."</c>.</param>
    /// <returns><see langword="true"/> if the path is registered as exempt; otherwise <see langword="false"/>.</returns>
    public bool IsExempt(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !jsonPath.StartsWith("$.", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = jsonPath[2..].Split('.', StringSplitOptions.None);
        return IsExempt(segments);
    }

    internal bool IsExempt(ReadOnlySpan<string> currentSegments)
    {
        foreach (var rule in exemptions)
        {
            if (rule.Length != currentSegments.Length)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < rule.Length; i++)
            {
                if (!string.Equals(rule[i], currentSegments[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The supported way to test whether a JSON property name is treated as sensitive.
    /// </summary>
    /// <remarks>
    /// Matching is <b>substring</b>, case-insensitive: a pattern matches if it occurs anywhere in
    /// <paramref name="propertyName"/>, not only as a full match. <c>"password"</c> therefore also
    /// matches <c>"newPassword"</c>, <c>"currentPassword"</c>, and <c>"passwordChangedAt"</c>;
    /// <c>"secret"</c> also matches <c>"client_secret"</c>; <c>"token"</c> also matches
    /// <c>"id_token"</c>, <c>"access_token_hash"</c>, and <c>"tokenCount"</c>. This is a deliberate choice: under-redaction
    /// is a security failure (a secret ships in the clear), over-redaction is a usability cost (a
    /// harmless field like <c>tokenCount</c> shows <c>"[redacted]"</c>) — the exact-match behaviour this
    /// replaced let real field names such as <c>newPassword</c> and <c>client_secret</c> pass through
    /// unredacted, which is the failure mode this method exists to prevent. The default deny-list cannot
    /// be removed — only added to via <see cref="AddPattern"/> — so this always returns
    /// <see langword="true"/> for a default pattern (or a superstring of one) regardless of what else has
    /// been registered.
    /// </remarks>
    /// <param name="propertyName">A JSON property name.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="propertyName"/> contains a configured pattern as a
    /// substring.
    /// </returns>
    public bool IsSensitive(string propertyName)
    {
        foreach (var pattern in patterns)
        {
            if (propertyName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
