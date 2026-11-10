using System.Security.Claims;
using Microsoft.Extensions.Logging;
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

    /// <summary>Same sequence as <see cref="IssueAsync"/> for a session that carries a second-factor proof: the proof is stored under the
    /// new session before the access token exists, so a token never claims a proof the server did not keep (and a refresh, which reads
    /// the stored row, keeps it). If anything fails after the refresh token is issued, nobody is handed it, so it is revoked.</summary>
    public static async Task<AuthTokens> IssueWithProofAsync(
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        ISessionProofStore proofs,
        TimeProvider timeProvider,
        ILogger logger,
        User user,
        string authenticationType,
        SessionProofSnapshot proof,
        CancellationToken cancellationToken)
    {
        var principal = await principalFactory.CreateAsync(user, authenticationType, cancellationToken).ConfigureAwait(false);
        var refresh = await refreshTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await proofs.SaveAsync(user.Id, refresh.FamilyId, proof, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The second-factor proof could not be stored for the new session.");
            }

            SessionProofClaims.Apply(principal, proof);
            AddSession(principal, refresh.FamilyId);
            var access = accessTokens.Issue(principal);
            return new AuthTokens(access.Token, ExpiresInSeconds(timeProvider, access.ExpiresAt), refresh.RawToken);
        }
        catch
        {
            await RevokeUnhandedAsync(refreshTokens, logger, refresh.RawToken, user.Id).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RevokeUnhandedAsync(IRefreshTokenService refreshTokens, ILogger logger, string rawRefreshToken, Guid userId)
    {
        try
        {
            await refreshTokens.RevokeAsync(rawRefreshToken, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception revokeFailure)
        {
            logger.LogError(revokeFailure, "Revoking the refresh token of a session whose proof or access token failed to issue also failed (user {UserId}).", userId);
        }
    }

    /// <summary>Sets the <c>sid</c> claim naming the refresh-token family (the session) the access token is for. Any
    /// <c>sid</c> already on the principal (a stored user or role claim of that type) is dropped first, so it cannot
    /// shadow the real session id when the claim is read back.</summary>
    public static void AddSession(ClaimsPrincipal principal, Guid familyId)
    {
        foreach (var identity in principal.Identities)
        {
            foreach (var stale in identity.FindAll(SessionClaims.SessionId).ToList())
            {
                identity.RemoveClaim(stale);
            }
        }

        principal.Identities.First().AddClaim(new Claim(SessionClaims.SessionId, familyId.ToString()));
    }

    /// <summary>The whole seconds remaining until <paramref name="expiresAt"/>, never negative.</summary>
    public static int ExpiresInSeconds(TimeProvider timeProvider, DateTimeOffset expiresAt) =>
        (int)Math.Max(0, (expiresAt - timeProvider.GetUtcNow()).TotalSeconds);
}
