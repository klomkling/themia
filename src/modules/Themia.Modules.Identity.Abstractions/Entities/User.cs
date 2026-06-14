using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>
/// A user account. Tenant-scoped when <see cref="ITenantEntity.TenantId"/> is set; a platform
/// (cross-tenant) super-admin when it is <see langword="null"/>.
/// </summary>
public sealed class User : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The login name, as entered by the user.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The upper-invariant normalized login name used for lookups and uniqueness.</summary>
    public string NormalizedUserName { get; set; } = string.Empty;

    /// <summary>The email address, as entered.</summary>
    public string? Email { get; set; }

    /// <summary>The upper-invariant normalized email used for lookups and uniqueness.</summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>Whether the email address has been confirmed.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>The phone number, as entered.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Whether the phone number has been confirmed.</summary>
    public bool PhoneNumberConfirmed { get; set; }

    /// <summary>The argon2id password hash, or <see langword="null"/> when no password is set.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>A random value reissued whenever credentials change; invalidates stale principals.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Whether the account is enabled. Disabled accounts cannot authenticate.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The number of consecutive failed password verifications since the last success.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>When the account lockout ends, or <see langword="null"/> when not locked out.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Whether lockout is enforced for this account.</summary>
    public bool LockoutEnabled { get; set; } = true;

    /// <summary>Whether two-factor authentication is enabled (the 0.5.0 hook; TOTP arrives later).</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Assigns the identifier for a new (transient) user.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
