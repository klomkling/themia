using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Default <see cref="IAuthTokenIssuer"/>. Every call reloads the user in the expected tenant (never trusts a snapshot), checks active,
/// lockout and tenant, and only then issues: the refresh token first, so the access token can name its session in <c>sid</c>, and the
/// refresh row is revoked again if the access token cannot be issued. A new session asks the <see cref="ISecondFactorPolicy"/>; a
/// renewal does not, because it must name a live session of the same user, and a session only exists once the factor was passed.
/// </summary>
internal sealed class GuardedAuthTokenIssuer : IAuthTokenIssuer
{
    private readonly IUserService users;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly ISecondFactorPolicy policy;
    private readonly ISecondFactorTicketService? tickets;
    private readonly ChannelSessionMinter minter;
    private readonly IdentityModuleOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GuardedAuthTokenIssuer> logger;

    public GuardedAuthTokenIssuer(
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        ISecondFactorPolicy policy,
        IEnumerable<IIdentityEventObserver> observers,
        IdentityModuleOptions options,
        TimeProvider timeProvider,
        ILogger<GuardedAuthTokenIssuer> logger,
        ISecondFactorTicketService? tickets = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.policy = policy;
        this.tickets = tickets;
        minter = new ChannelSessionMinter(principalFactory, accessTokens, refreshTokens, observers, timeProvider, logger);
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<IssueResult> StartSessionAsync(Guid userId, SessionRequest request, CancellationToken cancellationToken = default)
    {
        ChannelSessionMinter.Validate(request);
        using (TenantContextAccessor.BeginScope(request.ExpectedTenant))
        {
            var (user, refusal) = await LoadAndGuardAsync(userId, request, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return refusal;
            }

            var pending = await SecondFactorGate.RequireAsync(
                policy, tickets, user, SecondFactorMethod.Custom, request.Channel, false, false, cancellationToken).ConfigureAwait(false);
            if (pending is { } owed)
            {
                logger.LogInformation("User {UserId} owes a second factor on channel {Channel} ({Stage}).", user.Id, request.Channel, owed.Stage);
                return IssueResult.RequiresSecondFactor(owed);
            }

            return await IssueNewSessionAsync(user, request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IssueResult> CompleteSessionAsync(
        Guid userId, string ticket, SecondFactorStage expectedStage, SessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        ChannelSessionMinter.Validate(request);

        // Burnt first, whatever follows: a ticket is spent by the attempt to use it.
        var data = await (tickets ?? throw new InvalidOperationException("No ISecondFactorTicketService is registered."))
            .ConsumeAsync(ticket, cancellationToken).ConfigureAwait(false);
        if (data is null
            || data.Method != SecondFactorMethod.Custom
            || data.UserId != userId
            || data.Stage != expectedStage
            || !string.Equals(data.Provider, request.Channel, StringComparison.Ordinal)
            || !Equals(data.TenantId, request.ExpectedTenant))
        {
            logger.LogWarning("Session completion refused on channel {Channel}: the ticket is unknown, used, expired, or not for this user, stage, channel and tenant.", request.Channel);
            return IssueResult.Refused(IssueOutcome.TicketInvalid);
        }

        using (TenantContextAccessor.BeginScope(request.ExpectedTenant))
        {
            var (user, refusal) = await LoadAndGuardAsync(userId, request, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return refusal;
            }

            // The user changed since the primary check (password, phone, ...): the ticket no longer vouches for them.
            if (!string.Equals(user.SecurityStamp, data.SecurityStamp, StringComparison.Ordinal))
            {
                logger.LogWarning("Session completion refused for user {UserId}: the security stamp changed since the ticket was issued.", user.Id);
                return IssueResult.Refused(IssueOutcome.TicketInvalid);
            }

            return await IssueNewSessionAsync(user, request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IssueResult> RenewSessionAsync(Guid userId, Guid sessionId, SessionRequest request, CancellationToken cancellationToken = default)
    {
        ChannelSessionMinter.Validate(request);
        using (TenantContextAccessor.BeginScope(request.ExpectedTenant))
        {
            var (user, refusal) = await LoadAndGuardAsync(userId, request, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return refusal;
            }

            // The policy is not asked here, so this check is what stops a forged or foreign session id from skipping it: the session
            // must be a live, unrevoked, unexpired family of this very user in this tenant.
            if (!await refreshTokens.IsSessionLiveAsync(user.Id, sessionId, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning("Session renewal refused for user {UserId}: the session is not live.", user.Id);
                return IssueResult.Refused(IssueOutcome.SessionNotLive);
            }

            var principal = await minter.BuildPrincipalAsync(user, request, cancellationToken).ConfigureAwait(false);
            AuthTokenIssuer.AddSession(principal, sessionId);
            var access = accessTokens.Issue(principal);
            var tokens = new AuthTokens(access.Token, AuthTokenIssuer.ExpiresInSeconds(timeProvider, access.ExpiresAt), string.Empty);
            return IssueResult.Issued(tokens, sessionId);
        }
    }

    /// <summary>Reloads the user in the expected tenant and applies the guards. Null user means refused, with the reason.</summary>
    private async Task<(User? User, IssueResult Refusal)> LoadAndGuardAsync(Guid userId, SessionRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);

        // One answer for "not found", "another tenant's user" and "a platform user in a tenant scope": the caller learns nothing about which.
        if (user is null
            || !Equals(user.TenantId, request.ExpectedTenant)
            || (user.TenantId is null && !options.AllowPlatformLogin))
        {
            logger.LogWarning("Session refused on channel {Channel}: the user is not visible in the expected tenant.", request.Channel);
            return (null, IssueResult.Refused(IssueOutcome.TenantMismatch));
        }

        if (!user.IsActive)
        {
            logger.LogWarning("Session refused for user {UserId} on channel {Channel}: inactive.", user.Id, request.Channel);
            return (null, IssueResult.Refused(IssueOutcome.Inactive));
        }

        if (user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            logger.LogWarning("Session refused for user {UserId} on channel {Channel}: locked out.", user.Id, request.Channel);
            return (null, IssueResult.Refused(IssueOutcome.LockedOut));
        }

        return (user, default);
    }

    private async Task<IssueResult> IssueNewSessionAsync(User user, SessionRequest request, CancellationToken cancellationToken)
    {
        var session = await minter.MintAsync(user, request, cancellationToken).ConfigureAwait(false);
        return IssueResult.Issued(session.Tokens, session.SessionId);
    }
}
