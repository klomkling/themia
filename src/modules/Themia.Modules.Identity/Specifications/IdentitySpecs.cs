using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Specifications;

/// <summary>Finds a user by id in any tenant. Reserved for paths a secret has already proved (see
/// <c>IdentityScope.ResolveUserAnyTenantAsync</c>).</summary>
internal sealed class UserByIdAnyTenantSpec : Specification<User>
{
    public UserByIdAnyTenantSpec(Guid id)
    {
        Where(u => u.Id == id);
        WithoutTenantFilter();
    }
}

/// <summary>Active users holding a normalized phone number, in every tenant and the platform. Only for the
/// opt-in cross-tenant uniqueness check and by the OTP recipient resolver (exactly one match proceeds); both
/// callers read only ids, tenants and the phone number from the result.</summary>
internal sealed class ActiveUsersByNormalizedPhoneAnyTenantSpec : Specification<User>
{
    public ActiveUsersByNormalizedPhoneAnyTenantSpec(string normalizedPhone)
    {
        Where(u => u.NormalizedPhoneNumber == normalizedPhone && u.IsActive);
        WithoutTenantFilter();
    }
}

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

/// <summary>Finds a user by normalized phone number within the ambient tenant.</summary>
internal sealed class UserByNormalizedPhoneSpec : Specification<User>
{
    public UserByNormalizedPhoneSpec(string normalizedPhone) =>
        Where(u => u.NormalizedPhoneNumber == normalizedPhone);
}

/// <summary>Finds a platform (global) user by normalized phone number, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedPhoneSpec : Specification<User>
{
    public PlatformUserByNormalizedPhoneSpec(string normalizedPhone)
    {
        Where(u => u.NormalizedPhoneNumber == normalizedPhone && u.TenantId == null);
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

/// <summary>One token row while it is still unconsumed and unexpired: the predicate of the atomic consume.</summary>
internal sealed class ClaimableUserTokenSpec : Specification<UserToken>
{
    public ClaimableUserTokenSpec(Guid tokenId, DateTimeOffset now) =>
        Where(t => t.Id == tokenId && t.ConsumedAt == null && t.ExpiresAt > now);
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

/// <summary>The single refresh token with the given id. No tenant column; the owning user is resolved in scope by the service.</summary>
internal sealed class RefreshTokenByIdSpec : Specification<RefreshToken>
{
    public RefreshTokenByIdSpec(Guid id) => Where(t => t.Id == id);
}

/// <summary>A user's rotated tokens whose sealed successor is older than the reuse grace: nothing can replay them any more, so the sealed value
/// is dead weight to clear.</summary>
internal sealed class AgedSuccessorCiphertextByUserSpec : Specification<RefreshToken>
{
    public AgedSuccessorCiphertextByUserSpec(Guid userId, DateTimeOffset consumedBefore) =>
        Where(t => t.UserId == userId && t.SuccessorCiphertext != null && t.ConsumedAt < consumedBefore);
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

/// <summary>A user's non-expired, non-revoked tokens in one family: proof the family is a live session of that user.</summary>
internal sealed class ActiveRefreshTokensByUserAndFamilySpec : Specification<RefreshToken>
{
    public ActiveRefreshTokensByUserAndFamilySpec(Guid userId, Guid familyId, DateTimeOffset now) =>
        Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now);
}

/// <summary>A live session as the owner's CURRENT stamp sees it: unrevoked, unexpired, and bound to that stamp (or not bound
/// yet, a row issued before the column existed).</summary>
internal sealed class LiveRefreshSessionSpec : Specification<RefreshToken>
{
    public LiveRefreshSessionSpec(Guid userId, Guid familyId, DateTimeOffset now, string currentStamp) =>
        Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now
            && (t.SecurityStamp == currentStamp || t.SecurityStamp == null));
}

/// <summary>A user's unrevoked tokens still bound to <c>stamp</c>: exactly the rows a role or claim change moves to the new stamp. Used by a
/// set-based UPDATE, so only the stamp column is written and a revoke committed meanwhile cannot be overwritten.</summary>
internal sealed class UnrevokedRefreshTokensBoundToSpec : Specification<RefreshToken>
{
    public UnrevokedRefreshTokensBoundToSpec(Guid userId, string stamp) =>
        Where(t => t.UserId == userId && t.RevokedAt == null && t.SecurityStamp == stamp);
}

/// <summary>A user's non-expired, non-revoked tokens in every family but one (for revoke-all-except-session).</summary>
internal sealed class ActiveRefreshTokensByUserExceptFamilySpec : Specification<RefreshToken>
{
    public ActiveRefreshTokensByUserExceptFamilySpec(Guid userId, Guid keepFamilyId, DateTimeOffset now) =>
        Where(t => t.UserId == userId && t.FamilyId != keepFamilyId && t.RevokedAt == null && t.ExpiresAt > now);
}

/// <summary>The external-login link matching a provider and provider subject within the ambient
/// tenant. The framework tenant filter isolates it, so the same external account can map to a
/// different user per tenant.</summary>
internal sealed class ExternalLoginByProviderKeySpec : Specification<ExternalLoginLink>
{
    public ExternalLoginByProviderKeySpec(string provider, string externalId) =>
        Where(l => l.Provider == provider && l.ExternalId == externalId);
}

/// <summary>A user's external-login links within the ambient tenant, oldest first. Optionally narrowed
/// to one provider.</summary>
internal sealed class ExternalLoginsByUserSpec : Specification<ExternalLoginLink>
{
    public ExternalLoginsByUserSpec(Guid userId, string? provider = null)
    {
        if (provider is null)
        {
            Where(l => l.UserId == userId);
        }
        else
        {
            Where(l => l.UserId == userId && l.Provider == provider);
        }

        AddOrderBy(l => l.CreatedAt, descending: false);
    }
}

/// <summary>The external-login links of any of the given users within the ambient tenant.</summary>
internal sealed class ExternalLoginsByUsersSpec : Specification<ExternalLoginLink>
{
    public ExternalLoginsByUsersSpec(IReadOnlyCollection<Guid> userIds)
    {
        Where(l => userIds.Contains(l.UserId));
        AddOrderBy(l => l.CreatedAt, descending: false);
    }
}

/// <summary>Finds a platform (global) external-login link by provider and subject, bypassing the tenant
/// filter. The <c>TenantId == null</c> predicate guarantees only a genuine platform link matches — never
/// another tenant's — so it is safe to resolve from a tenant scope (mirrors the platform user specs and
/// the <c>AllowPlatformLogin</c> fallback in <c>IUserService.FindByEmailAsync</c>).</summary>
internal sealed class PlatformExternalLoginByProviderKeySpec : Specification<ExternalLoginLink>
{
    public PlatformExternalLoginByProviderKeySpec(string provider, string externalId)
    {
        Where(l => l.Provider == provider && l.ExternalId == externalId && l.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Memberships of a role. The join carries no tenant column; the caller has already resolved the role in scope.</summary>
internal sealed class UserRolesByRoleSpec : Specification<UserRole>
{
    public UserRolesByRoleSpec(Guid roleId) => Where(ur => ur.RoleId == roleId);
}

/// <summary>Users by id in any tenant, for a set-based stamp rotation. The ids come from memberships of a role that
/// resolved in scope, or from a user the caller already resolved in scope.</summary>
internal sealed class UsersByIdsAnyTenantSpec : Specification<User>
{
    public UsersByIdsAnyTenantSpec(IReadOnlyCollection<Guid> ids)
    {
        Where(u => ids.Contains(u.Id));
        WithoutTenantFilter();
    }
}

/// <summary>One user in any tenant, only while the stored stamp is still the one the caller read: the conditional write that makes
/// "rotate from S1" fail instead of overwriting a rotation that committed in between. The caller has already resolved the user in its scope.</summary>
internal sealed class UserByIdAndStampAnyTenantSpec : Specification<User>
{
    public UserByIdAndStampAnyTenantSpec(Guid id, string stamp)
    {
        Where(u => u.Id == id && u.SecurityStamp == stamp);
        WithoutTenantFilter();
    }
}

/// <summary>The single token matching a purpose and exact (deterministic SHA-256) hash. The hash column is unique and
/// the raw token is high-entropy, so the match names the owner without a user id from the caller.</summary>
internal sealed class TokenByPurposeAndHashSpec : Specification<UserToken>
{
    public TokenByPurposeAndHashSpec(TokenPurpose purpose, string tokenHash) =>
        Where(t => t.Purpose == purpose && t.TokenHash == tokenHash);
}

/// <summary>Every refresh token row of a user, live or not (erasure deletes the hashes too).</summary>
internal sealed class RefreshTokensByUserSpec : Specification<RefreshToken>
{
    public RefreshTokensByUserSpec(Guid userId) => Where(t => t.UserId == userId);
}

/// <summary>Every one-time token row of a user, whatever its purpose or state.</summary>
internal sealed class UserTokensByUserSpec : Specification<UserToken>
{
    public UserTokensByUserSpec(Guid userId) => Where(t => t.UserId == userId);
}
