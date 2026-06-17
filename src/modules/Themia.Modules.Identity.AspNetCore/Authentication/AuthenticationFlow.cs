using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

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
        TimeProvider timeProvider,
        ILogger<AuthenticationFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.passwordHasher = passwordHasher;
        this.hooks = hooks;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        var before = new BeforeLoginContext(userName);
        await hooks.OnBeforeLoginAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, before.DenialReason).ConfigureAwait(false);
        }

        var verification = await users.VerifyPasswordAsync(userName, password, cancellationToken).ConfigureAwait(false);
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
            return await FailAsync(userName, Map(verification), failure, cancellationToken).ConfigureAwait(false);
        }

        var user = await users.FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailAsync(userName, LoginFailureReason.NotFound, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        var succeeded = new LoginSucceededContext(user);
        await hooks.OnLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, succeeded.DenialReason).ConfigureAwait(false);
        }

        var tokens = await IssueAsync(user, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("User {UserId} authenticated via password.", user.Id);
        return LoginResult.Success(tokens);
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
            return RefreshRotationResult.Denied();
        }

        var rotation = await refreshTokens.ValidateAndRotateAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (!rotation.TryGetSuccess(out var user, out var replacement))
        {
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
            return RefreshRotationResult.Denied();
        }

        logger.LogInformation("Access token refreshed for user {UserId}.", user.Id);
        return RefreshRotationResult.Success(tokens);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        await refreshTokens.RevokeAsync(refreshToken, allSessions, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Logout for refresh token (allSessions={AllSessions}).", allSessions);
        await hooks.OnLogoutAsync(new LogoutContext(allSessions), cancellationToken).ConfigureAwait(false);
    }

    private Task<AuthTokens> IssueAsync(User user, CancellationToken cancellationToken) =>
        AuthTokenIssuer.IssueAsync(principalFactory, accessTokens, refreshTokens, timeProvider, user, AuthenticationType, cancellationToken);

    private async Task<LoginResult> FailAsync(string userName, LoginFailureReason reason, LoginResult result, CancellationToken cancellationToken, string? denialReason = null)
    {
        if (denialReason is null)
        {
            logger.LogWarning("Login failed for {UserName}: {Reason}.", userName, reason);
        }
        else
        {
            logger.LogWarning("Login failed for {UserName}: {Reason} ({DenialReason}).", userName, reason, denialReason);
        }

        await hooks.OnLoginFailedAsync(new LoginFailedContext(userName, reason), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static LoginFailureReason Map(PasswordVerificationResult verification) => verification switch
    {
        PasswordVerificationResult.NotFound => LoginFailureReason.NotFound,
        PasswordVerificationResult.Inactive => LoginFailureReason.Inactive,
        PasswordVerificationResult.LockedOut => LoginFailureReason.LockedOut,
        _ => LoginFailureReason.WrongPassword,
    };
}
