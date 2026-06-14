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
    /// <c>TenantId == null</c>) entity. The bypass is scoped to this single SaveChangesAsync; callers
    /// save one entity per call (no co-tracked tenant entities are flushed under the bypass).</summary>
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
