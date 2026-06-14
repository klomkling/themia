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
    private readonly IRepository<UserClaim, Guid> userClaims;
    private readonly IRepository<RoleClaim, Guid> roleClaims;
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IRepository<User, Guid> users;
    private readonly IRepository<Role, Guid> roles;
    private readonly IUnitOfWork unitOfWork;

    /// <summary>Creates the service.</summary>
    public ClaimService(
        IRepository<UserClaim, Guid> userClaims,
        IRepository<RoleClaim, Guid> roleClaims,
        IRepository<UserRole, Guid> memberships,
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IUnitOfWork unitOfWork)
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
    }

    /// <inheritdoc />
    public async Task AddUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        // Resolve the parent through the tenant-filtered repo: the child table carries no tenant_id,
        // so a cross-tenant write must be refused here (fail-closed — it is genuine misuse).
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"User '{userId}' was not found in the current tenant scope.");
        }

        var claim = new UserClaim { UserId = userId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await userClaims.AddAsync(claim, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        // Refuse if the parent user is outside the ambient tenant scope (child table has no tenant_id).
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        var existing = await userClaims.FirstOrDefaultAsync(new UserClaimMatchSpec(userId, claimType, claimValue), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        userClaims.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        // Resolve the parent through the tenant-filtered repo (child table carries no tenant_id).
        if (await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"Role '{roleId}' was not found in the current tenant scope.");
        }

        var claim = new RoleClaim { RoleId = roleId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await roleClaims.AddAsync(claim, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        // Refuse if the parent role is outside the ambient tenant scope (child table has no tenant_id).
        if (await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        var existing = await roleClaims.FirstOrDefaultAsync(new RoleClaimMatchSpec(roleId, claimType, claimValue), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        roleClaims.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Resolve the parent user through the tenant-filtered repo; if out of scope, expose nothing.
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        var direct = await userClaims.ListAsync(new UserClaimsByUserSpec(userId), cancellationToken).ConfigureAwait(false);

        var roleIds = (await memberships.ListAsync(new Specifications.UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();

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
