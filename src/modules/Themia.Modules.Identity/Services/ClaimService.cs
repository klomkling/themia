using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IClaimService"/> over the Themia data abstractions.</summary>
public sealed class ClaimService : IClaimService
{
    private const int StampRotationBatchSize = 500;

    private readonly IRepository<UserClaim, Guid> userClaims;
    private readonly IRepository<RoleClaim, Guid> roleClaims;
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IRepository<User, Guid> users;
    private readonly IRepository<Role, Guid> roles;
    private readonly IUnitOfWork unitOfWork;
    private readonly IMemoryCache? stampCache;
    private readonly IRepository<RefreshToken, Guid>? refreshTokens;

    /// <summary>Creates the service without a stamp cache to evict.</summary>
    public ClaimService(
        IRepository<UserClaim, Guid> userClaims,
        IRepository<RoleClaim, Guid> roleClaims,
        IRepository<UserRole, Guid> memberships,
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IUnitOfWork unitOfWork)
        : this(userClaims, roleClaims, memberships, users, roles, unitOfWork, null)
    {
    }

    /// <summary>Creates the service.</summary>
    /// <param name="userClaims">The user-claim repository.</param>
    /// <param name="roleClaims">The role-claim repository.</param>
    /// <param name="memberships">The user-role repository.</param>
    /// <param name="users">The user repository.</param>
    /// <param name="roles">The role repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="stampCache">The cache the security-stamp validator uses, evicted for each user whose stamp rotates; may be null.</param>
    public ClaimService(
        IRepository<UserClaim, Guid> userClaims,
        IRepository<RoleClaim, Guid> roleClaims,
        IRepository<UserRole, Guid> memberships,
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IUnitOfWork unitOfWork,
        IMemoryCache? stampCache)
        : this(userClaims, roleClaims, memberships, users, roles, unitOfWork, stampCache, null)
    {
    }

    /// <summary>Creates the service.</summary>
    /// <param name="userClaims">The user-claim repository.</param>
    /// <param name="roleClaims">The role-claim repository.</param>
    /// <param name="memberships">The user-role repository.</param>
    /// <param name="users">The user repository.</param>
    /// <param name="roles">The role repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="stampCache">The cache the security-stamp validator uses; may be null.</param>
    /// <param name="refreshTokens">The refresh-token repository: a role-claim change rebinds the holders' live sessions to
    /// their new stamp. When null the sessions end at their next refresh instead.</param>
    public ClaimService(
        IRepository<UserClaim, Guid> userClaims,
        IRepository<RoleClaim, Guid> roleClaims,
        IRepository<UserRole, Guid> memberships,
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IUnitOfWork unitOfWork,
        IMemoryCache? stampCache,
        IRepository<RefreshToken, Guid>? refreshTokens)
    {
        ArgumentNullException.ThrowIfNull(userClaims);
        ArgumentNullException.ThrowIfNull(roleClaims);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        this.userClaims = userClaims;
        this.roleClaims = roleClaims;
        this.memberships = memberships;
        this.users = users;
        this.roles = roles;
        this.unitOfWork = unitOfWork;
        this.stampCache = stampCache;
        this.refreshTokens = refreshTokens;
    }

    /// <inheritdoc />
    public async Task AddUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ThrowIfReserved(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        // The parent must resolve in the ambient tenant or as a platform user; a cross-tenant
        // user is refused (fail-closed — it is genuine misuse).
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"User '{userId}' was not found in the current tenant scope.");
        }

        var claim = new UserClaim { UserId = userId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await ChangeAsync(
            async ct =>
            {
                await userClaims.AddAsync(claim, ct).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            _ => Task.FromResult<IReadOnlyCollection<Guid>>([userId]),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        // Refuse if the parent user is neither in the ambient tenant nor a platform user.
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var existing = await userClaims.FirstOrDefaultAsync(new UserClaimMatchSpec(userId, claimType, claimValue), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await ChangeAsync(
            async ct =>
            {
                userClaims.Remove(existing);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            _ => Task.FromResult<IReadOnlyCollection<Guid>>([userId]),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ThrowIfReserved(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        // The parent must resolve in the ambient tenant or as a platform role; cross-tenant is refused.
        if (!await IdentityScope.RoleExistsAsync(roles, roleId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Role '{roleId}' was not found in the current tenant scope.");
        }

        var claim = new RoleClaim { RoleId = roleId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await ChangeAsync(
            async ct =>
            {
                await roleClaims.AddAsync(claim, ct).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            ct => HolderIdsAsync(roleId, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        // Refuse if the parent role is neither in the ambient tenant nor a platform role.
        if (!await IdentityScope.RoleExistsAsync(roles, roleId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var existing = await roleClaims.FirstOrDefaultAsync(new RoleClaimMatchSpec(roleId, claimType, claimValue), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await ChangeAsync(
            async ct =>
            {
                roleClaims.Remove(existing);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            },
            ct => HolderIdsAsync(roleId, ct),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<IReadOnlyCollection<Guid>> HolderIdsAsync(Guid roleId, CancellationToken cancellationToken) =>
        (await memberships.ListAsync(new UserRolesByRoleSpec(roleId), cancellationToken).ConfigureAwait(false))
            .Select(m => m.UserId)
            .ToList();

    // Apps read permissions from the access token's claims, so a claim change must cut the tokens issued before it:
    // the claim write and the stamp rotation of every affected user commit together, and the in-process cache is
    // evicted only after the commit. The holders are read inside the transaction, after the write, so a user assigned
    // to the role in between is still rotated. The update runs in batches: Dapper sends one parameter per id and
    // SQL Server caps a command at 2,100, which a large role would exceed.
    private async Task ChangeAsync(
        Func<CancellationToken, Task> write,
        Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> affectedUserIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> rotated = [];
        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await write(ct).ConfigureAwait(false);
                rotated = await affectedUserIds(ct).ConfigureAwait(false);
                foreach (var batch in rotated.Chunk(StampRotationBatchSize))
                {
                    var stamp = SecurityStampCache.NewStamp();
                    await users.UpdateWhereAsync(
                        new UsersByIdsAnyTenantSpec(batch),
                        set => set.Set(u => u.SecurityStamp, stamp),
                        ct).ConfigureAwait(false);
                    // Same value for the users and their live sessions: the sessions are still legitimate (refresh
                    // rebuilds the claims from the database), so they follow the stamp instead of being ended by it.
                    await RefreshStampBinding.RebindAsync(refreshTokens, batch, stamp, ct).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, rotated);
    }

    // Themia sets these itself; a stored claim of such a type could forge or shadow the issued one. Removing a stored
    // row of a reserved type stays allowed, so rows written before this rule can be cleaned up.
    private static void ThrowIfReserved(string claimType)
    {
        if (ReservedClaimTypes.IsReserved(claimType))
        {
            throw new ArgumentException($"Claim type '{claimType}' is reserved: Themia sets it itself.", nameof(claimType));
        }
    }

    /// <inheritdoc />
    // RS0027: mirrors the interface — the convenience overload keeps the optional token; the role-ids
    // overload below has more parameters but a required token, so only one overload is optional (RS0026).
#pragma warning disable RS0027
    public async Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default)
#pragma warning restore RS0027
    {
        // Resolve the parent user in the ambient tenant or as a platform user; if neither, expose nothing.
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var roleIds = (await memberships.ListAsync(new Specifications.UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();

        return await GetEffectiveClaimsAsync(userId, roleIds, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var direct = await userClaims.ListAsync(new UserClaimsByUserSpec(userId), cancellationToken).ConfigureAwait(false);

        var fromRoles = roleIds.Count == 0
            ? []
            : await roleClaims.ListAsync(new RoleClaimsByRoleIdsSpec(roleIds), cancellationToken).ConfigureAwait(false);

        // Union by (type, value); deduplicate so a claim granted both directly and via a role appears once.
        var seen = new HashSet<(string, string)>();
        var result = new List<Claim>();
        foreach (var (type, value) in direct.Select(c => (c.ClaimType, c.ClaimValue))
                     .Concat(fromRoles.Select(c => (c.ClaimType, c.ClaimValue))))
        {
            if (seen.Add((type, value)))
            {
                result.Add(new Claim(type, value));
            }
        }
        return result;
    }
}
