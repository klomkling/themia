using System.Security.Claims;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Builds a <see cref="ClaimsPrincipal"/> from a user — the single source of what goes into the principal (used by cookie auth in 0.5.0 and JWT issuance in 0.5.1).</summary>
public interface IClaimsPrincipalFactory
{
    /// <summary>Creates the claims principal for a user, including role claims and the effective claim set.</summary>
    /// <param name="user">The user.</param>
    /// <param name="authenticationType">The authentication type to stamp on the identity (e.g. <c>"Identity.Application"</c> or <c>"Bearer"</c>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The constructed principal.</returns>
    Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default);
}
