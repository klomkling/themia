using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Mints a signed access token from a claims principal. The principal is the single source
/// of "what's in the token" across cookie and JWT.</summary>
public interface IAccessTokenService
{
    /// <summary>Builds a signed access token carrying the principal's claims.</summary>
    /// <param name="principal">The principal produced by <c>IClaimsPrincipalFactory</c>.</param>
    /// <returns>The minted token and its expiry.</returns>
    AccessToken Issue(ClaimsPrincipal principal);
}
