using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Creates and assigns <see cref="Role"/> records.</summary>
public interface IRoleService
{
    /// <summary>Creates a role in the ambient tenant (or platform scope when no tenant is ambient).</summary>
    /// <param name="name">The role name.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The new role id, or null when a same-named role already exists in scope.</returns>
    Task<Guid?> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Finds a role by normalized name within the ambient tenant (then platform scope).</summary>
    /// <param name="name">The role name (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The role, or null.</returns>
    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Assigns a role to a user. Both must resolve within the ambient tenant scope.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="roleId">The role id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when assigned (or already assigned); false when either side is not found in scope.</returns>
    Task<bool> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Removes a role from a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="roleId">The role id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a membership was removed.</returns>
    Task<bool> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Lists the role ids assigned to a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The assigned role ids.</returns>
    Task<IReadOnlyList<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}
