namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A persisted, single-use, expiring token (email/phone confirmation, password reset, 2FA). The raw token is never stored — only its hash.</summary>
public sealed class UserToken
{
    /// <summary>The token identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>What the token authorizes.</summary>
    public TokenPurpose Purpose { get; set; }

    /// <summary>The hash of the raw token value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>When the token expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the token was consumed, or <see langword="null"/> while still valid.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Assigns the identifier for a new (transient) token.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
