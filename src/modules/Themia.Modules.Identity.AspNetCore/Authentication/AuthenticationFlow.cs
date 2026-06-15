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

    /// <summary>Creates the flow.</summary>
    public AuthenticationFlow(
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IPasswordHasher passwordHasher,
        IAuthenticationHooks hooks,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.users = users;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.passwordHasher = passwordHasher;
        this.hooks = hooks;
        this.timeProvider = timeProvider;
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
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken).ConfigureAwait(false);
        }

        var verification = await users.VerifyPasswordAsync(userName, password, cancellationToken).ConfigureAwait(false);
        if (verification != PasswordVerificationResult.Success)
        {
            if (verification is PasswordVerificationResult.NotFound or PasswordVerificationResult.Inactive)
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
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken).ConfigureAwait(false);
        }

        var tokens = await IssueAsync(user, cancellationToken).ConfigureAwait(false);
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
            return RefreshRotationResult.Denied();
        }

        var rotation = await refreshTokens.ValidateAndRotateAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        switch (rotation.Outcome)
        {
            case RefreshOutcome.ReuseDetected:
                return RefreshRotationResult.ReuseDetected();
            case RefreshOutcome.Invalid:
                return RefreshRotationResult.Invalid();
        }

        var user = rotation.User!;
        var replacement = rotation.Replacement!.Value;
        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var access = accessTokens.Issue(principal);
        var tokens = new AuthTokens(access.Token, ExpiresInSeconds(access.ExpiresAt), replacement.RawToken);

        // The rotation has already persisted. A late deny here returns a uniform 401; the (valid but
        // undelivered) successor simply expires unused — acceptable per the access-token tradeoff.
        var refreshSucceeded = new RefreshSucceededContext(user);
        await hooks.OnRefreshSucceededAsync(refreshSucceeded, cancellationToken).ConfigureAwait(false);
        if (refreshSucceeded.IsDenied)
        {
            return RefreshRotationResult.Denied();
        }

        return RefreshRotationResult.Success(tokens);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        await refreshTokens.RevokeAsync(refreshToken, allSessions, cancellationToken).ConfigureAwait(false);
        await hooks.OnLogoutAsync(new LogoutContext(allSessions), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthTokens> IssueAsync(User user, CancellationToken cancellationToken)
    {
        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var access = accessTokens.Issue(principal);
        var refresh = await refreshTokens.IssueAsync(user.Id, null, cancellationToken).ConfigureAwait(false);
        return new AuthTokens(access.Token, ExpiresInSeconds(access.ExpiresAt), refresh.RawToken);
    }

    private async Task<LoginResult> FailAsync(string userName, LoginFailureReason reason, LoginResult result, CancellationToken cancellationToken)
    {
        await hooks.OnLoginFailedAsync(new LoginFailedContext(userName, reason), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private int ExpiresInSeconds(DateTimeOffset expiresAt) =>
        (int)Math.Max(0, (expiresAt - timeProvider.GetUtcNow()).TotalSeconds);

    private static LoginFailureReason Map(PasswordVerificationResult verification) => verification switch
    {
        PasswordVerificationResult.NotFound => LoginFailureReason.NotFound,
        PasswordVerificationResult.Inactive => LoginFailureReason.Inactive,
        PasswordVerificationResult.LockedOut => LoginFailureReason.LockedOut,
        _ => LoginFailureReason.WrongPassword,
    };
}
