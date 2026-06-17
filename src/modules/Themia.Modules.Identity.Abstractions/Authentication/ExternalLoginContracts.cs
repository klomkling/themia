using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The result of resolving or provisioning a user from an external identity.</summary>
/// <param name="User">The resolved or created user.</param>
/// <param name="WasCreated">True if a new user was provisioned.</param>
/// <param name="WasLinked">True if a new link row was created (create or auto-link).</param>
public readonly record struct ExternalLoginResult(User User, bool WasCreated, bool WasLinked);

/// <summary>Resolves an existing external link, auto-links by verified email, or provisions a new
/// password-less user — all within the ambient tenant scope.</summary>
public interface IExternalLoginService
{
    /// <summary>Resolves the user for an external identity, creating/linking per the 0.5.2 policy
    /// (existing link → user; verified-email match → link; else create).</summary>
    /// <param name="identity">The normalized external identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The resolve-or-provision result.</returns>
    Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity identity, CancellationToken cancellationToken = default);
}
