using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Shared platform-aware parent resolution and global-aware save logic for the Identity
/// services. Centralizes the security-critical "resolve in the ambient tenant, else fall back to a
/// genuine platform (<c>TenantId == null</c>) row" pattern and the tenant write-validation bypass so
/// the rules hold by construction across every caller.</summary>
internal static class IdentityScope
{
    /// <summary>Normalizes a lookup key (user name, email, role name) to its canonical form. This MUST
    /// stay byte-identical to the comparison the DB filtered unique indexes perform, so the in-memory
    /// duplicate check and the at-rest uniqueness constraint agree.</summary>
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    /// <summary>Resolves a user in the ambient tenant, falling back to a genuine platform user
    /// (<c>TenantId == null</c>). The platform spec's predicate ensures another tenant's user is
    /// never matched, so cross-tenant access stays refused.</summary>
    public static async Task<User?> ResolveUserAsync(IReadRepository<User, Guid> users, Guid id, CancellationToken ct)
    {
        var u = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        return u ?? await users.FirstOrDefaultAsync(new PlatformUserByIdSpec(id), ct).ConfigureAwait(false);
    }

    /// <summary>True if the user resolves in the ambient tenant or as a genuine platform user.</summary>
    public static async Task<bool> UserExistsAsync(IReadRepository<User, Guid> users, Guid id, CancellationToken ct)
        => await ResolveUserAsync(users, id, ct).ConfigureAwait(false) is not null;

    /// <summary>Resolves a role in the ambient tenant, falling back to a genuine platform role
    /// (<c>TenantId == null</c>). The platform spec's predicate ensures another tenant's role is
    /// never matched, so cross-tenant access stays refused.</summary>
    public static async Task<Role?> ResolveRoleAsync(IReadRepository<Role, Guid> roles, Guid id, CancellationToken ct)
    {
        var r = await roles.GetByIdAsync(id, ct).ConfigureAwait(false);
        return r ?? await roles.FirstOrDefaultAsync(new PlatformRoleByIdSpec(id), ct).ConfigureAwait(false);
    }

    /// <summary>True if the role resolves in the ambient tenant or as a genuine platform role.</summary>
    public static async Task<bool> RoleExistsAsync(IReadRepository<Role, Guid> roles, Guid id, CancellationToken ct)
        => await ResolveRoleAsync(roles, id, ct).ConfigureAwait(false) is not null;

    /// <summary>Saves changes, bypassing the tenant write-validation ONLY for a global (platform,
    /// <c>TenantId == null</c>) entity.
    /// <para><b>Contract — call only with a single pending change.</b> The bypass suppresses tenant
    /// write-validation for the WHOLE unit of work, not just the platform entity. Any other tenant
    /// entity co-tracked on the same context would therefore also skip validation when the platform
    /// branch runs. Every current caller stages exactly one <c>Update</c>/<c>Remove</c> before this
    /// call, so the bypass is safe by construction. Making the bypass per-entity is a framework
    /// concern tracked as a follow-up; until then, do not flush co-tracked tenant entities here.</para></summary>
    public static async Task SaveScopedAsync(IUnitOfWork unitOfWork, IDataFilterScope filterScope, bool isPlatform, CancellationToken ct)
    {
        if (isPlatform)
        {
            using (filterScope.BypassTenantFilter())
            {
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
