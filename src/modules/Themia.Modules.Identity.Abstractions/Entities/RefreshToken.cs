namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A revocable, rotating refresh token. A parent-keyed child of <see cref="User"/> with no
/// tenant column — tenant isolation is enforced at the service layer by resolving the owning user in
/// scope. The raw token is never stored; only its SHA-256 hash.</summary>
public sealed class RefreshToken
{
    /// <summary>The token identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Deterministic SHA-256 (Base64) hash of the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Groups a rotation chain for reuse-detection and family revocation.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Absolute expiry.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the token is rotated (single redemption); otherwise null.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Set on logout / revoke-all / reuse-detection; otherwise null.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Successor row id in the rotation chain; null until rotated.</summary>
    public Guid? ReplacedById { get; set; }

    /// <summary>The id of the token this one replaced; carries a filtered unique index so a parent can be
    /// rotated only once. Null for freshly-issued tokens; set only on a rotation successor.</summary>
    public Guid? ReplacedTokenId { get; set; }

    /// <summary>Issue time, set by the service via <see cref="TimeProvider"/> (forensics).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Assigns the identifier for a new (transient) token.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
