namespace Themia.Challenges;

/// <summary>
/// Identity of a challenge. Tenant is part of it deliberately: two tenants may hold the same phone
/// number, so without it a code issued to one tenant would verify under another.
/// </summary>
/// <param name="Key">The opaque key — a phone number, an email address, a user id. Never parsed.</param>
/// <param name="Purpose">Scopes the challenge and selects its configuration.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a platform-level challenge.</param>
public sealed record ChallengeScope(string Key, string Purpose, string? TenantId = null)
{
    /// <summary>
    /// Renders the scope for logs and diagnostics with <see cref="Key"/> masked. A positional record's
    /// compiler-generated <c>ToString()</c> prints every property including <see cref="Key"/> — a phone
    /// number or an email address, i.e. PII — which a "{Scope}" log template would then leak verbatim.
    /// Only the last 4 characters survive, enough to correlate a scope across log lines without
    /// reconstructing the key. <see cref="Purpose"/> and <see cref="TenantId"/> are not sensitive and
    /// are rendered in full.
    /// </summary>
    /// <returns>A string representation safe to log.</returns>
    public override string ToString() =>
        $"ChallengeScope {{ Key = {MaskKey(Key)}, Purpose = {Purpose}, TenantId = {TenantId ?? "(none)"} }}";

    // Same masking shape as Themia.Notifications.Providers.RecipientRedaction.Mask: keep the last 4
    // characters for correlation, mask the rest. Reimplemented here rather than referenced because
    // that type is internal to Themia.Notifications, and Themia.Challenges has no dependency on it.
    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(none)";
        }

        return key.Length <= 4 ? "****" : new string('*', key.Length - 4) + key[^4..];
    }
}
