using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>Default <see cref="IAuthenticationFlow"/>. Owns the security-critical sequence
/// (gate → verify → timing-equalize → principal build → issue) and invokes <see cref="IAuthenticationHooks"/>
/// at fixed points. Every credential failure (including a hook deny) yields a non-success result that the
/// endpoints collapse to a uniform 401.</summary>
public sealed class AuthenticationFlow : IAuthenticationFlow
{
    private const string AuthenticationType = "Bearer";

    private readonly IUserService users;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IPasswordHasher passwordHasher;
    private readonly IAuthenticationHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AuthenticationFlow> logger;

    /// <summary>Creates the flow.</summary>
    public AuthenticationFlow(
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IPasswordHasher passwordHasher,
        IAuthenticationHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        TimeProvider timeProvider,
        ILogger<AuthenticationFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.passwordHasher = passwordHasher;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Back-compatible overload matching the pre-<see cref="IIdentityEventObserver"/> signature. Forwards
    /// with an empty observer collection, so a manually constructed instance — e.g. in an adopter's own
    /// test — keeps compiling and behaves exactly as before. DI resolves the greediest constructor it can
    /// satisfy, and <see cref="IEnumerable{T}"/> is always resolvable (empty when nothing is registered),
    /// so a live app always picks the constructor above and observes any registered observers.
    /// </summary>
    public AuthenticationFlow(
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IPasswordHasher passwordHasher,
        IAuthenticationHooks hooks,
        TimeProvider timeProvider,
        ILogger<AuthenticationFlow> logger)
        : this(users, principalFactory, accessTokens, refreshTokens, passwordHasher, hooks,
              Array.Empty<IIdentityEventObserver>(), timeProvider, logger)
    {
    }

    /// <summary>Invokes <paramref name="invoke"/> against every registered observer. A throwing observer
    /// must not change the flow it observes, so each invocation is individually caught and logged at
    /// <c>Error</c>; a cancellation request still propagates.</summary>
    private async Task RaiseAsync(Func<IIdentityEventObserver, CancellationToken, Task> invoke, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await invoke(observer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Identity event observer {ObserverType} threw and was swallowed.", observer.GetType());
            }
        }
    }

    /// <summary>Resolves a reused refresh token's owner for audit attribution. A failure here must not
    /// turn a refresh rejection into a 500 — losing attribution is acceptable, breaking the flow is
    /// not — so any exception is logged at <c>Error</c> and swallowed to <see langword="null"/>.</summary>
    private async Task<Guid?> TryResolveOwnerAsync(string rawToken, CancellationToken cancellationToken)
    {
        try
        {
            return await refreshTokens.ResolveOwnerAsync(rawToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to resolve the owner of a reused refresh token for audit attribution.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<LoginResult> LoginAsync(string identifier, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(password);

        var before = new BeforeLoginContext(identifier);
        await hooks.OnBeforeLoginAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return await FailAsync(identifier, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, before.DenialReason).ConfigureAwait(false);
        }

        var (resolved, ambiguous) = await ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            // Equalize with the success path's argon2 cost. Resolution failing is a NEW early exit that
            // did not exist when the identifier was always a username, and without this the three
            // lookups plus no hash would be measurably faster than a wrong password — a timing oracle
            // over three identifier spaces rather than the one it replaced.
            _ = passwordHasher.Hash(password);

            var reason = ambiguous ? LoginFailureReason.AmbiguousIdentifier : LoginFailureReason.NotFound;
            return await FailAsync(identifier, reason, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        // Verification and the lockout state machine stay keyed on the USERNAME, whatever the caller
        // typed. Lockout counts attempts against an account; keying it on the identifier instead would
        // give each of a user's three identifiers its own independent budget, tripling the guesses
        // available before anything locks.
        var verification = await users.VerifyPasswordAsync(resolved.UserName, password, cancellationToken).ConfigureAwait(false);
        if (verification != PasswordVerificationResult.Success)
        {
            // Equalize latency across every "no real hash ran" path (NotFound/Inactive/LockedOut all
            // return before VerifyPasswordAsync runs argon2), so response time leaks no account state.
            if (verification is PasswordVerificationResult.NotFound
                             or PasswordVerificationResult.Inactive
                             or PasswordVerificationResult.LockedOut)
            {
                _ = passwordHasher.Hash(password);
            }

            var failure = verification == PasswordVerificationResult.LockedOut ? LoginResult.LockedOut() : LoginResult.InvalidCredentials();
            return await FailAsync(identifier, Map(verification), failure, cancellationToken).ConfigureAwait(false);
        }

        var succeeded = new LoginSucceededContext(resolved);
        await hooks.OnLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return await FailAsync(identifier, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, succeeded.DenialReason).ConfigureAwait(false);
        }

        var tokens = await IssueAsync(resolved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("User {UserId} authenticated via password.", resolved.Id);
        await RaiseAsync((o, ct) => o.OnLoginSucceededAsync(resolved.Id, resolved.UserName, ct), cancellationToken).ConfigureAwait(false);
        return LoginResult.Success(tokens);
    }

    /// <summary>
    /// Resolves an identifier to exactly one user across username, confirmed email and confirmed phone.
    /// </summary>
    /// <returns>
    /// The single matching user, or <see langword="null"/> with <c>Ambiguous</c> telling the caller which
    /// kind of nothing it was — no match, or more than one user.
    /// </returns>
    /// <remarks>
    /// <b>All three lookups always run, even after the first one matches.</b> Two reasons, and the second
    /// is why short-circuiting on username would be wrong even though username has priority:
    /// <list type="number">
    /// <item><description>
    /// A collision is only visible if you look. Per-column uniqueness cannot prevent user A's username
    /// equalling user B's email; stopping at the first hit resolves that to A and hands A's account to
    /// whoever knows B's password — or the reverse, depending on which column was checked first.
    /// </description></item>
    /// <item><description>
    /// The work is then identical for every identifier. Resolving a username in one query and a phone in
    /// three would make the number of round trips a signal for which identifier space a string belongs
    /// to, which is the enumeration oracle this whole path is written to avoid.
    /// </description></item>
    /// </list>
    /// <para>
    /// Matching the same user on two columns — a user whose username IS their email — is not a collision.
    /// The check is on distinct user ids, not on how many columns matched.
    /// </para>
    /// </remarks>
    private async Task<(User? User, bool Ambiguous)> ResolveAsync(string identifier, CancellationToken cancellationToken)
    {
        var byUserName = await users.FindByUserNameAsync(identifier, cancellationToken).ConfigureAwait(false);
        var byEmail = await users.FindByEmailAsync(identifier, cancellationToken).ConfigureAwait(false);
        var byPhone = await users.FindByPhoneAsync(identifier, cancellationToken).ConfigureAwait(false);

        // An unconfirmed email or phone is a claim, not a proof of control, so it is not an identifier.
        // Dropped AFTER the lookup rather than filtered inside it, so the query count does not vary.
        if (byEmail is { EmailConfirmed: false })
        {
            byEmail = null;
        }

        if (byPhone is { PhoneNumberConfirmed: false })
        {
            byPhone = null;
        }

        // Priority order — username, then email, then phone — decides WHICH user is returned; it never
        // decides whether the result is ambiguous. Ambiguity is judged across all three first.
        var distinct = new HashSet<Guid>();
        foreach (var candidate in new[] { byUserName, byEmail, byPhone })
        {
            if (candidate is not null)
            {
                distinct.Add(candidate.Id);
            }
        }

        return distinct.Count switch
        {
            0 => (null, false),
            1 => (byUserName ?? byEmail ?? byPhone, false),
            _ => (null, true),
        };
    }

    /// <inheritdoc />
    public async Task<RefreshRotationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var before = new BeforeRefreshContext();
        await hooks.OnBeforeRefreshAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            logger.LogWarning("Refresh denied by hook: {DenialReason}.", before.DenialReason);
            // Pre-rotation: no state has changed and no user has been resolved yet.
            await RaiseAsync((o, ct) => o.OnRefreshDeniedAsync(null, before.DenialReason, rotationCommitted: false, ct), cancellationToken).ConfigureAwait(false);
            return RefreshRotationResult.Denied();
        }

        var rotation = await refreshTokens.ValidateAndRotateAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (!rotation.TryGetSuccess(out var user, out var replacement))
        {
            // ReuseDetected — a rotated refresh token presented twice — is the single most actionable
            // event in this flow: "account X's token was replayed" tells an operator to revoke that
            // account's sessions right now, where "some token was replayed" tells them nothing (there is
            // no other join key on an audit row for this event). The presented token's row still exists
            // (rotation stamps ConsumedAt/RevokedAt on it rather than deleting it), so it resolves.
            // Invalid has no owner by construction — an unknown/expired/out-of-scope token — and is left
            // null rather than paying for a lookup on every bad token an attacker sprays at the endpoint.
            var reuseUserId = rotation.Outcome == RefreshOutcome.ReuseDetected
                ? await TryResolveOwnerAsync(refreshToken, cancellationToken).ConfigureAwait(false)
                : null;
            await RaiseAsync((o, ct) => o.OnRefreshFailedAsync(reuseUserId, rotation.Outcome, ct), cancellationToken).ConfigureAwait(false);
            return rotation.Outcome switch
            {
                RefreshOutcome.ReuseDetected => RefreshRotationResult.ReuseDetected(),
                _ => RefreshRotationResult.Invalid(),
            };
        }

        // A deactivated or locked-out account must not keep minting tokens via refresh — otherwise
        // deactivation/lockout only takes effect when the refresh token finally expires (up to its full
        // lifetime). Mirrors the login gate (IUserService.VerifyPasswordAsync) and the external-login
        // gate via the shared UserLockoutExtensions predicate. The rotation already persisted; the
        // undelivered successor simply expires unused (same tradeoff as the late hook deny below).
        if (!user.IsActive || user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            logger.LogWarning("Refresh rejected for user {UserId}: account inactive or locked out.", user.Id);
            await RaiseAsync((o, ct) => o.OnRefreshFailedAsync(user.Id, RefreshOutcome.Invalid, ct), cancellationToken).ConfigureAwait(false);
            return RefreshRotationResult.Invalid();
        }

        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var access = accessTokens.Issue(principal);
        var tokens = new AuthTokens(access.Token, AuthTokenIssuer.ExpiresInSeconds(timeProvider, access.ExpiresAt), replacement.RawToken);

        // The rotation has already persisted. A late deny here returns a uniform 401; the (valid but
        // undelivered) successor simply expires unused — acceptable per the access-token tradeoff.
        var refreshSucceeded = new RefreshSucceededContext(user);
        await hooks.OnRefreshSucceededAsync(refreshSucceeded, cancellationToken).ConfigureAwait(false);
        if (refreshSucceeded.IsDenied)
        {
            logger.LogWarning("Refresh denied by hook: {DenialReason}.", refreshSucceeded.DenialReason);
            // Post-rotation: a valid successor token now exists that the client never received. Carrying
            // rotationCommitted:true lets an investigator tell this apart from the pre-rotation deny above.
            await RaiseAsync((o, ct) => o.OnRefreshDeniedAsync(user.UserName, refreshSucceeded.DenialReason, rotationCommitted: true, ct), cancellationToken).ConfigureAwait(false);
            return RefreshRotationResult.Denied();
        }

        logger.LogInformation("Access token refreshed for user {UserId}.", user.Id);
        await RaiseAsync((o, ct) => o.OnRefreshSucceededAsync(user.Id, ct), cancellationToken).ConfigureAwait(false);
        return RefreshRotationResult.Success(tokens);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        // Resolved BEFORE revocation so the audit trail can attribute the logout to a user even though
        // revocation itself does not return one. Routed through TryResolveOwnerAsync: losing attribution
        // is acceptable, but a transient failure here must never skip revocation — logout must always
        // revoke the session.
        var userId = await TryResolveOwnerAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        await refreshTokens.RevokeAsync(refreshToken, allSessions, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Logout for refresh token (allSessions={AllSessions}).", allSessions);
        await hooks.OnLogoutAsync(new LogoutContext(userId, allSessions), cancellationToken).ConfigureAwait(false);
        await RaiseAsync((o, ct) => o.OnLogoutAsync(userId, allSessions, ct), cancellationToken).ConfigureAwait(false);
    }

    private Task<AuthTokens> IssueAsync(User user, CancellationToken cancellationToken) =>
        AuthTokenIssuer.IssueAsync(principalFactory, accessTokens, refreshTokens, timeProvider, user, AuthenticationType, cancellationToken);

    private async Task<LoginResult> FailAsync(string identifier, LoginFailureReason reason, LoginResult result, CancellationToken cancellationToken, string? denialReason = null)
    {
        // Masked. This line used to carry a username; now that the identifier may be an email address or
        // a phone number, logging it verbatim would push PII into every log aggregator on every failed
        // login — the highest-volume line in the flow. Enough tail survives to correlate one attacker's
        // attempts across lines without reconstructing the address.
        var masked = Mask(identifier);

        if (denialReason is null)
        {
            logger.LogWarning("Login failed for {Identifier}: {Reason}.", masked, reason);
        }
        else
        {
            logger.LogWarning("Login failed for {Identifier}: {Reason} ({DenialReason}).", masked, reason, denialReason);
        }

        // Hooks get the identifier UNMASKED: they are the adopter's own code, run in-process, and are the
        // documented place to build lockout, alerting or abuse detection — all of which need the real
        // value. The masking above is about what leaves the process in a log line.
        await hooks.OnLoginFailedAsync(new LoginFailedContext(identifier, reason), cancellationToken).ConfigureAwait(false);

        // A hook denial is a distinct event from every other failure reason — it is the adopter's own
        // policy refusing the attempt, not a bad credential — so it gets its own observer method.
        if (reason == LoginFailureReason.Denied)
        {
            await RaiseAsync((o, ct) => o.OnLoginDeniedAsync(identifier, denialReason, ct), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RaiseAsync((o, ct) => o.OnLoginFailedAsync(identifier, reason, ct), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Keeps the last 4 characters, masks the rest. Same shape as the redaction in
    /// <c>Themia.Notifications</c> and <c>Themia.Challenges</c>.</summary>
    private static string Mask(string value) =>
        value.Length <= 4 ? "****" : new string('*', value.Length - 4) + value[^4..];

    private static LoginFailureReason Map(PasswordVerificationResult verification) => verification switch
    {
        PasswordVerificationResult.NotFound => LoginFailureReason.NotFound,
        PasswordVerificationResult.Inactive => LoginFailureReason.Inactive,
        PasswordVerificationResult.LockedOut => LoginFailureReason.LockedOut,
        _ => LoginFailureReason.WrongPassword,
    };
}
