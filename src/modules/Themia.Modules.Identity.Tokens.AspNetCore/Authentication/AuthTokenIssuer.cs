using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

/// <summary>Builds a Themia access + refresh token pair for a user. Shared by the password
/// (<c>AuthenticationFlow</c>) and external-login (<c>ExternalAuthenticationFlow</c>) flows so both
/// mint structurally identical first-class tokens — any change to the issue sequence (claims, audit,
/// session binding) lands in one place instead of drifting between two copies.</summary>
internal static class AuthTokenIssuer
{
    /// <summary>Builds the principal, issues the access token, and issues a fresh refresh token.</summary>
    public static async Task<AuthTokens> IssueAsync(
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        TimeProvider timeProvider,
        User user,
        string authenticationType,
        CancellationToken cancellationToken)
    {
        var principal = await principalFactory.CreateAsync(user, authenticationType, cancellationToken).ConfigureAwait(false);
        // The refresh family is the session, so it is issued first and its id goes into the access token.
        var refresh = await refreshTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
        AddSession(principal, refresh.FamilyId);
        var access = accessTokens.Issue(principal);
        return new AuthTokens(access.Token, ExpiresInSeconds(timeProvider, access.ExpiresAt), refresh.RawToken);
    }

    /// <summary>Adds the <c>sid</c> claim naming the refresh-token family (the session) the access token is for.</summary>
    public static void AddSession(ClaimsPrincipal principal, Guid familyId) =>
        principal.Identities.First().AddClaim(new Claim(SessionClaims.SessionId, familyId.ToString()));

    /// <summary>The whole seconds remaining until <paramref name="expiresAt"/>, never negative.</summary>
    public static int ExpiresInSeconds(TimeProvider timeProvider, DateTimeOffset expiresAt) =>
        (int)Math.Max(0, (expiresAt - timeProvider.GetUtcNow()).TotalSeconds);
}
