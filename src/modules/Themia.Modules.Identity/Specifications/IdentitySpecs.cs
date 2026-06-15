using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Specifications;

/// <summary>Finds a user by normalized user name within the ambient tenant.</summary>
internal sealed class UserByNormalizedNameSpec : Specification<User>
{
    public UserByNormalizedNameSpec(string normalizedUserName) =>
        Where(u => u.NormalizedUserName == normalizedUserName);
}

/// <summary>Finds a platform (global) user by normalized user name, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedNameSpec : Specification<User>
{
    public PlatformUserByNormalizedNameSpec(string normalizedUserName)
    {
        Where(u => u.NormalizedUserName == normalizedUserName && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a user by normalized email within the ambient tenant.</summary>
internal sealed class UserByNormalizedEmailSpec : Specification<User>
{
    public UserByNormalizedEmailSpec(string normalizedEmail) =>
        Where(u => u.NormalizedEmail == normalizedEmail);
}

/// <summary>Finds a platform (global) user by normalized email, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedEmailSpec : Specification<User>
{
    public PlatformUserByNormalizedEmailSpec(string normalizedEmail)
    {
        Where(u => u.NormalizedEmail == normalizedEmail && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a platform (global) user by id, bypassing the tenant filter. The
/// <c>TenantId == null</c> predicate guarantees only a genuine platform user matches — never
/// another tenant's row — so it is safe to resolve outside the ambient tenant scope.</summary>
internal sealed class PlatformUserByIdSpec : Specification<User>
{
    public PlatformUserByIdSpec(Guid id)
    {
        Where(u => u.Id == id && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a platform (global) role by id, bypassing the tenant filter. The
/// <c>TenantId == null</c> predicate guarantees only a genuine platform role matches — never
/// another tenant's row — so it is safe to resolve outside the ambient tenant scope.</summary>
internal sealed class PlatformRoleByIdSpec : Specification<Role>
{
    public PlatformRoleByIdSpec(Guid id)
    {
        Where(r => r.Id == id && r.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a role by normalized name within the ambient tenant.</summary>
internal sealed class RoleByNormalizedNameSpec : Specification<Role>
{
    public RoleByNormalizedNameSpec(string normalizedName) =>
        Where(r => r.NormalizedName == normalizedName);
}

/// <summary>Finds a platform (global) role by normalized name, bypassing the tenant filter.</summary>
internal sealed class PlatformRoleByNormalizedNameSpec : Specification<Role>
{
    public PlatformRoleByNormalizedNameSpec(string normalizedName)
    {
        Where(r => r.NormalizedName == normalizedName && r.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>All membership rows for a user.</summary>
internal sealed class UserRolesByUserSpec : Specification<UserRole>
{
    public UserRolesByUserSpec(Guid userId) => Where(ur => ur.UserId == userId);
}

/// <summary>A specific user–role membership row.</summary>
internal sealed class UserRoleSpec : Specification<UserRole>
{
    public UserRoleSpec(Guid userId, Guid roleId) => Where(ur => ur.UserId == userId && ur.RoleId == roleId);
}

/// <summary>All direct claims of a user.</summary>
internal sealed class UserClaimsByUserSpec : Specification<UserClaim>
{
    public UserClaimsByUserSpec(Guid userId) => Where(c => c.UserId == userId);
}

/// <summary>A specific user-claim row (for removal).</summary>
internal sealed class UserClaimMatchSpec : Specification<UserClaim>
{
    public UserClaimMatchSpec(Guid userId, string claimType, string claimValue) =>
        Where(c => c.UserId == userId && c.ClaimType == claimType && c.ClaimValue == claimValue);
}

/// <summary>A specific role-claim row (for removal).</summary>
internal sealed class RoleClaimMatchSpec : Specification<RoleClaim>
{
    public RoleClaimMatchSpec(Guid roleId, string claimType, string claimValue) =>
        Where(c => c.RoleId == roleId && c.ClaimType == claimType && c.ClaimValue == claimValue);
}

/// <summary>All claims belonging to any of the given roles.</summary>
internal sealed class RoleClaimsByRoleIdsSpec : Specification<RoleClaim>
{
    public RoleClaimsByRoleIdsSpec(IReadOnlyCollection<Guid> roleIds) =>
        Where(c => roleIds.Contains(c.RoleId));
}

/// <summary>The single token matching a user, purpose, and exact (deterministic SHA-256) hash.
/// Querying by hash avoids loading every token for the pair — the raw-token hash is high-entropy
/// and irreversible, so an exact DB match leaks nothing about the presented token.</summary>
internal sealed class TokenByUserPurposeAndHashSpec : Specification<UserToken>
{
    public TokenByUserPurposeAndHashSpec(Guid userId, TokenPurpose purpose, string tokenHash) =>
        Where(t => t.UserId == userId && t.Purpose == purpose && t.TokenHash == tokenHash);
}

/// <summary>All roles whose id is in the given set, resolved without the tenant filter but still
/// guarded to platform (null-tenant) roles or roles of the ambient tenant.</summary>
internal sealed class RolesByIdsSpec : Specification<Role>
{
    public RolesByIdsSpec(IReadOnlyCollection<Guid> roleIds, TenantId? ambientTenantId)
    {
        // The bypass lets platform (null-tenant) roles resolve from a tenant scope; the predicate still
        // excludes other tenants' roles, so a stray cross-tenant membership row can never leak a role.
        Where(r => roleIds.Contains(r.Id) && (r.TenantId == null || r.TenantId == ambientTenantId));
        WithoutTenantFilter();
    }
}

/// <summary>The single refresh token matching an exact (deterministic SHA-256) hash. No tenant column
/// exists; the owning user is resolved in scope by the service, which is what enforces isolation.</summary>
internal sealed class RefreshTokenByHashSpec : Specification<RefreshToken>
{
    public RefreshTokenByHashSpec(string tokenHash) => Where(t => t.TokenHash == tokenHash);
}

/// <summary>A family's not-yet-revoked tokens — the rows a family revocation actually flips.</summary>
internal sealed class ActiveRefreshTokensByFamilySpec : Specification<RefreshToken>
{
    public ActiveRefreshTokensByFamilySpec(Guid familyId) =>
        Where(t => t.FamilyId == familyId && t.RevokedAt == null);
}

/// <summary>A user's non-expired, non-revoked tokens (for revoke-all).</summary>
internal sealed class ActiveRefreshTokensByUserSpec : Specification<RefreshToken>
{
    public ActiveRefreshTokensByUserSpec(Guid userId, DateTimeOffset now) =>
        Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now);
}
