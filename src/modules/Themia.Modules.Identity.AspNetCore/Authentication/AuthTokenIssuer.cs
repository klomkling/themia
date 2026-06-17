using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>Builds a Themia access + refresh token pair for a user. Shared by the password
/// (<see cref="AuthenticationFlow"/>) and external-login (<c>ExternalAuthenticationFlow</c>) flows so both
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
        var access = accessTokens.Issue(principal);
        var refresh = await refreshTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new AuthTokens(access.Token, ExpiresInSeconds(timeProvider, access.ExpiresAt), refresh.RawToken);
    }

    /// <summary>The whole seconds remaining until <paramref name="expiresAt"/>, never negative.</summary>
    public static int ExpiresInSeconds(TimeProvider timeProvider, DateTimeOffset expiresAt) =>
        (int)Math.Max(0, (expiresAt - timeProvider.GetUtcNow()).TotalSeconds);
}
