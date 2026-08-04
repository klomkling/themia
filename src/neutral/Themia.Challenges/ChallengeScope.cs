namespace Themia.Challenges;

/// <summary>
/// Identity of a challenge. Tenant is part of it deliberately: two tenants may hold the same phone
/// number, so without it a code issued to one tenant would verify under another.
/// </summary>
/// <param name="Key">The opaque key — a phone number, an email address, a user id. Never parsed.</param>
/// <param name="Purpose">Scopes the challenge and selects its configuration.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a platform-level challenge.</param>
public sealed record ChallengeScope(string Key, string Purpose, string? TenantId = null);
