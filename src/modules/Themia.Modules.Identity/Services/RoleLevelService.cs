using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IRoleLevelService"/> over the Themia data abstractions.</summary>
public sealed class RoleLevelService : IRoleLevelService
{
    private readonly IRepository<Role, Guid> roles;
    private readonly IUnitOfWork unitOfWork;
    private readonly IDataFilterScope filterScope;

    /// <summary>Creates the service.</summary>
    public RoleLevelService(IRepository<Role, Guid> roles, IUnitOfWork unitOfWork, IDataFilterScope filterScope)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(filterScope);
        this.roles = roles;
        this.unitOfWork = unitOfWork;
        this.filterScope = filterScope;
    }

    /// <inheritdoc />
    public async Task<Guid?> CreateAsync(string name, RoleLevel level, string? description = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateLevel(level);
        var normalized = IdentityScope.Normalize(name);
        if (await roles.AnyAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var role = new Role { Name = name, NormalizedName = normalized, Description = description, Level = level };
        role.SetId(Guid.CreateVersion7());
        await roles.AddAsync(role, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }

    /// <inheritdoc />
    public async Task<bool> SetLevelAsync(Guid roleId, RoleLevel level, CancellationToken cancellationToken = default)
    {
        ValidateLevel(level);
        var role = await IdentityScope.ResolveRoleAsync(roles, roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return false;
        }

        role.Level = level;
        roles.Update(role);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, role.TenantId is null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void ValidateLevel(RoleLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Must be Level1 to Level5.");
        }
    }
}
