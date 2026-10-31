using Microsoft.Extensions.Caching.Memory;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IRoleService"/> over the Themia data abstractions.</summary>
public sealed class RoleService : IRoleService
{
    private readonly IRepository<User, Guid> users;
    private readonly IRepository<Role, Guid> roles;
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IUnitOfWork unitOfWork;
    private readonly IDataFilterScope filterScope;
    private readonly IMemoryCache? stampCache;
    private readonly IRepository<RefreshToken, Guid>? refreshTokens;

    /// <summary>Creates the service with a default filter scope.</summary>
    public RoleService(
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork)
        : this(users, roles, memberships, unitOfWork, new DataFilterScope())
    {
    }

    /// <summary>Creates the service without a refresh-token store to rebind.</summary>
    // RS0027: this overload shipped with the optional stamp cache; the one below adds the refresh-token store and, without a
    // default, keeps a call with six or fewer arguments unambiguous.
#pragma warning disable RS0027
    public RoleService(
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork,
        IDataFilterScope filterScope,
        IMemoryCache? stampCache = null)
        : this(users, roles, memberships, unitOfWork, filterScope, stampCache, null)
    {
    }
#pragma warning restore RS0027

    /// <summary>Creates the service.</summary>
    /// <param name="users">The user repository.</param>
    /// <param name="roles">The role repository.</param>
    /// <param name="memberships">The user-role repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="filterScope">The data filter scope used for platform-aware writes.</param>
    /// <param name="stampCache">The cache the security-stamp validator uses; may be null.</param>
    /// <param name="refreshTokens">The refresh-token repository: a membership change rebinds the user's live sessions to the
    /// new stamp. When null the sessions end at their next refresh instead.</param>
    public RoleService(
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork,
        IDataFilterScope filterScope,
        IMemoryCache? stampCache,
        IRepository<RefreshToken, Guid>? refreshTokens)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(filterScope);
        this.filterScope = filterScope;
        this.stampCache = stampCache;
        this.refreshTokens = refreshTokens;
        this.users = users;
        this.roles = roles;
        this.memberships = memberships;
        this.unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<Guid?> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = IdentityScope.Normalize(name);
        if (await roles.AnyAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var role = new Role { Name = name, NormalizedName = normalized, Description = description };
        role.SetId(Guid.CreateVersion7());
        await roles.AddAsync(role, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }

    /// <inheritdoc />
    public async Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = IdentityScope.Normalize(name);
        var inTenant = await roles.FirstOrDefaultAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
        return inTenant ?? await roles.FirstOrDefaultAsync(new PlatformRoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // Both sides must resolve in the ambient tenant or as platform rows; cross-tenant is refused.
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }
        if (!await IdentityScope.RoleExistsAsync(roles, roleId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var existing = await memberships.FirstOrDefaultAsync(new UserRoleSpec(userId, roleId), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return true;   // idempotent
        }

        var membership = new UserRole { UserId = userId, RoleId = roleId };
        membership.SetId(Guid.CreateVersion7());
        await ChangeMembershipAsync(
            user,
            async ct =>
            {
                await memberships.AddAsync(membership, ct).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // Both sides must resolve in the ambient tenant or as platform rows; cross-tenant is refused.
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }
        if (!await IdentityScope.RoleExistsAsync(roles, roleId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var existing = await memberships.FirstOrDefaultAsync(new UserRoleSpec(userId, roleId), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await ChangeMembershipAsync(
            user,
            async ct =>
            {
                memberships.Remove(existing);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A role change must cut live access tokens (their role claims are now stale). The stamp rotation and the
    // membership change commit together: saved apart, a login in between would mint a token with the new stamp and the
    // old roles, which then validates. The stamp is saved first inside the transaction, and the cached stamp is evicted
    // only after the commit.
    private async Task ChangeMembershipAsync(User user, Func<CancellationToken, Task> change, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                user.SecurityStamp = SecurityStampCache.NewStamp();
                users.Update(user);
                await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, ct).ConfigureAwait(false);
                // The session is still legitimate (refresh rebuilds the roles from the database): follow the new stamp, not end.
                await RefreshStampBinding.RebindAsync(refreshTokens, [user.Id], user.SecurityStamp, ct).ConfigureAwait(false);
                await change(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, [user.Id]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Resolve the parent user in the ambient tenant or as a platform user; if neither, expose nothing.
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var rows = await memberships.ListAsync(new UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.RoleId).ToList();
    }
}
