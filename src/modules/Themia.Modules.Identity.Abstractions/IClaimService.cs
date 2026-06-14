using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Manages user and role claims and computes a user's effective claim set.</summary>
public interface IClaimService
{
    /// <summary>Adds a claim directly to a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when the user is not found in the current tenant scope.</exception>
    Task AddUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Removes a matching claim from a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a claim was removed.</returns>
    Task<bool> RemoveUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Adds a claim to a role.</summary>
    /// <param name="roleId">The role id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when the role is not found in the current tenant scope.</exception>
    Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Removes a matching claim from a role.</summary>
    /// <param name="roleId">The role id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a claim was removed.</returns>
    Task<bool> RemoveRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Computes the union of a user's direct claims and the claims of every role assigned to the user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The distinct effective claims.</returns>
    // RS0027: this convenience overload (optional token) intentionally has fewer parameters than the
    // role-ids overload below, whose token is required to avoid two optional-parameter overloads (RS0026).
#pragma warning disable RS0027
    Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default);
#pragma warning restore RS0027

    /// <summary>Computes the union of a user's direct claims and the claims of the given roles, using
    /// role ids the caller has already resolved from the user's memberships. This overload skips the
    /// membership re-query and the user-existence guard, so callers that already hold the user and its
    /// role ids (such as the principal factory) avoid the redundant round-trips.</summary>
    /// <param name="userId">The user id whose direct claims are unioned in.</param>
    /// <param name="roleIds">The role ids already resolved from the user's memberships.</param>
    /// <param name="cancellationToken">A cancellation token (required — see the overload above).</param>
    /// <returns>The distinct effective claims.</returns>
    Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
}
