using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserLevelReader"/>: reads the user's memberships and roles on every call, so a
/// role change or a level change is seen at once (the level is deliberately never cached or put in a token).</summary>
public sealed class UserLevelReader : IUserLevelReader
{
    private readonly IReadRepository<User, Guid> users;
    private readonly IReadRepository<Role, Guid> roles;
    private readonly IReadRepository<UserRole, Guid> memberships;
    private readonly ITenantContext tenantContext;

    /// <summary>Creates the reader.</summary>
    public UserLevelReader(
        IReadRepository<User, Guid> users,
        IReadRepository<Role, Guid> roles,
        IReadRepository<UserRole, Guid> memberships,
        ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tenantContext);
        this.users = users;
        this.roles = roles;
        this.memberships = memberships;
        this.tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<RoleLevel> GetLevelAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            return RoleLevel.Level5;
        }

        var roleIds = (await memberships.ListAsync(new UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();
        if (roleIds.Count == 0)
        {
            return RoleLevel.Level5;
        }

        var rows = await roles.ListAsync(new RolesByIdsSpec(roleIds, tenantContext.CurrentTenantId), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.Level).DefaultIfEmpty(RoleLevel.Level5).Min();
    }
}
