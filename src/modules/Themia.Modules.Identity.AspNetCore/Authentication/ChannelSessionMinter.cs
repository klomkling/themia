using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>A session minted on a channel: the tokens and the id of its refresh-token family (the <c>sid</c>).</summary>
internal readonly record struct MintedSession(AuthTokens Tokens, Guid SessionId);

/// <summary>
/// The one place that mints a session carrying an app channel: the refresh token first (the access token names its family
/// in <c>sid</c>), the refresh row revoked again if the access token cannot be issued, and <c>OnSessionIssuedAsync</c> raised
/// with the channel. Shared by <see cref="GuardedAuthTokenIssuer"/>, the one-time-code flow's channel overload and the
/// second-factor completion, so a channel session can never be minted without its label by one of them.
/// </summary>
internal sealed partial class ChannelSessionMinter
{
    private const string AuthenticationType = "Bearer";

    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IIdentityEventObserver[] observers;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly ISessionProofStore? proofs;

    public ChannelSessionMinter(
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IEnumerable<IIdentityEventObserver> observers,
        TimeProvider timeProvider,
        ILogger logger,
        ISessionProofStore? proofs = null)
    {
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.proofs = proofs;
    }

    [GeneratedRegex("^[a-z0-9._-]{1,64}$")]
    private static partial Regex ChannelPattern();

    /// <summary>Checks the channel label and the app claims; throws <see cref="ArgumentException"/> on a bad one.</summary>
    public static void Validate(SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Channel) || !ChannelPattern().IsMatch(request.Channel))
        {
            throw new ArgumentException("The channel must be 1 to 64 characters of a-z, 0-9, '.', '_' and '-'.", nameof(request));
        }

        foreach (var claim in request.AppClaims ?? [])
        {
            if (ReservedClaimTypes.IsReserved(claim.Type))
            {
                throw new ArgumentException($"The claim type '{claim.Type}' is reserved and cannot be set by the app.", nameof(request));
            }
        }
    }

    public async Task<ClaimsPrincipal> BuildPrincipalAsync(User user, SessionRequest request, CancellationToken cancellationToken)
    {
        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        if (request.AppClaims is { Count: > 0 } appClaims && principal.Identities.FirstOrDefault() is { } identity)
        {
            // Replace by type, so a claim the app carries forward (a session start) is never duplicated.
            foreach (var type in appClaims.Select(c => c.Type).Distinct(StringComparer.Ordinal))
            {
                foreach (var stale in identity.FindAll(type).ToList())
                {
                    identity.RemoveClaim(stale);
                }
            }

            identity.AddClaims(appClaims);
        }

        return principal;
    }

    /// <summary>Mints a new session for <paramref name="user"/> on <paramref name="request"/>'s channel. The caller has already run every gate.</summary>
    public async Task<MintedSession> MintAsync(User user, SessionRequest request, CancellationToken cancellationToken)
    {
        // A token must never claim a proof the server did not store: without a store there is nowhere to keep it, so nothing is issued.
        if (request.Proof is not null && proofs is null)
        {
            throw new InvalidOperationException("The session request carries a second-factor proof but no ISessionProofStore is registered.");
        }

        var principal = await BuildPrincipalAsync(user, request, cancellationToken).ConfigureAwait(false);

        // The refresh family is the session, so it is issued first and its id goes into the access token.
        var refresh = await refreshTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
        AccessToken access;
        var issued = false;
        try
        {
            if (request.Proof is not null
                && !await proofs!.SaveAsync(user.Id, refresh.FamilyId, request.Proof, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The second-factor proof could not be stored for the new session.");
            }

            SessionProofClaims.Apply(principal, request.Proof);
            AuthTokenIssuer.AddSession(principal, refresh.FamilyId);
            access = accessTokens.Issue(principal);
            issued = true;
        }
        finally
        {
            if (!issued)
            {
                await RevokeUnhandedAsync(refresh.RawToken, user.Id).ConfigureAwait(false);
            }
        }

        var tokens = new AuthTokens(access.Token, AuthTokenIssuer.ExpiresInSeconds(timeProvider, access.ExpiresAt), refresh.RawToken);
        logger.LogInformation("Session {SessionId} issued for user {UserId} on channel {Channel}.", refresh.FamilyId, user.Id, request.Channel);
        await RaiseAsync((o, ct) => o.OnSessionIssuedAsync(user.Id, request.Channel, refresh.FamilyId, ct), cancellationToken).ConfigureAwait(false);
        return new MintedSession(tokens, refresh.FamilyId);
    }

    // Nobody will ever be handed this refresh token: end the session rather than leave a live row behind. Not tied to the
    // caller's cancellation (a cancelled request must still clean up) and never allowed to mask the original failure.
    private async Task RevokeUnhandedAsync(string rawRefreshToken, Guid userId)
    {
        try
        {
            await refreshTokens.RevokeAsync(rawRefreshToken, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception revokeFailure)
        {
            logger.LogError(revokeFailure, "Revoking the refresh token of a session whose access token failed to issue also failed (user {UserId}).", userId);
        }
    }

    private async Task RaiseAsync(Func<IIdentityEventObserver, CancellationToken, Task> invoke, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await invoke(observer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identity event observer {ObserverType} threw and was swallowed.", observer.GetType());
            }
        }
    }
}
