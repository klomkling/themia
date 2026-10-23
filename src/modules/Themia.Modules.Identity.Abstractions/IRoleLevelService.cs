namespace Themia.Modules.Identity.Abstractions;

/// <summary>Creates roles with a <see cref="RoleLevel"/> and changes the level of an existing role. Kept apart from
/// <see cref="IRoleService"/> so adding it does not break that interface's implementers.</summary>
public interface IRoleLevelService
{
    /// <summary>Creates a role at the given level in the ambient tenant.</summary>
    /// <param name="name">The role name; unique per tenant, case-insensitively.</param>
    /// <param name="level">The role's level.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The new role id, or <see langword="null"/> when the name is already taken.</returns>
    Task<Guid?> CreateAsync(string name, RoleLevel level, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Sets the level of a role in the ambient tenant or a platform role.</summary>
    /// <param name="roleId">The role.</param>
    /// <param name="level">The new level.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns><see langword="false"/> when the role does not resolve in scope.</returns>
    Task<bool> SetLevelAsync(Guid roleId, RoleLevel level, CancellationToken cancellationToken = default);
}
