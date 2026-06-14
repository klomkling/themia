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

    /// <summary>Creates the service.</summary>
    public RoleService(
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        this.users = users;
        this.roles = roles;
        this.memberships = memberships;
        this.unitOfWork = unitOfWork;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    // Resolves the parent in the ambient tenant OR as a genuine platform row (TenantId == null).
    // The platform spec refuses another tenant's row, so cross-tenant access stays closed.
    // The membership (UserRole) child rows carry no tenant_id, so no write bypass is needed here.
    private async Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not null
        || await users.FirstOrDefaultAsync(new PlatformUserByIdSpec(userId), cancellationToken).ConfigureAwait(false) is not null;

    private async Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken) =>
        await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false) is not null
        || await roles.FirstOrDefaultAsync(new PlatformRoleByIdSpec(roleId), cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async Task<Guid?> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = Normalize(name);
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
        var normalized = Normalize(name);
        var inTenant = await roles.FirstOrDefaultAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
        return inTenant ?? await roles.FirstOrDefaultAsync(new PlatformRoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // Both sides must resolve in the ambient tenant or as platform rows; cross-tenant is refused.
        if (!await UserExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (!await RoleExistsAsync(roleId, cancellationToken).ConfigureAwait(false))
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
        await memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // Both sides must resolve in the ambient tenant or as platform rows; cross-tenant is refused.
        if (!await UserExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (!await RoleExistsAsync(roleId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var existing = await memberships.FirstOrDefaultAsync(new UserRoleSpec(userId, roleId), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        memberships.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Resolve the parent user in the ambient tenant or as a platform user; if neither, expose nothing.
        if (!await UserExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var rows = await memberships.ListAsync(new UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.RoleId).ToList();
    }
}
