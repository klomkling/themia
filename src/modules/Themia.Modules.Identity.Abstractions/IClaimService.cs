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
    Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Computes the union of a user's direct claims and the claims of every role assigned to the user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The distinct effective claims.</returns>
    Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default);
}
