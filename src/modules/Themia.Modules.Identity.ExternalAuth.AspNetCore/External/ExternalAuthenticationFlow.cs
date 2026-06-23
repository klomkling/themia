using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.External;

/// <summary>Default <see cref="IExternalAuthenticationFlow"/>. Owns the external-login sequence
/// (provider resolve → before-gate → exchange → resolve/provision → post-gate → issue) and invokes
/// <see cref="IExternalAuthenticationHooks"/> at fixed points. Every non-success outcome (provider
/// missing/rejected or a hook deny) yields a typed result the endpoint collapses to a uniform 401
/// (except <see cref="ExternalLoginOutcome.ProviderNotFound"/> → 404). The token terminus mirrors the
/// 0.5.1 password flow exactly, so external tokens are first-class Themia access + refresh pairs.</summary>
public sealed class ExternalAuthenticationFlow : IExternalAuthenticationFlow
{
    private const string AuthenticationType = "Bearer";

    private readonly IExternalAuthProviderRegistry registry;
    private readonly IExternalLoginService externalLogins;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IExternalAuthenticationHooks hooks;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ExternalAuthenticationFlow> logger;

    /// <summary>Creates the flow.</summary>
    public ExternalAuthenticationFlow(
        IExternalAuthProviderRegistry registry,
        IExternalLoginService externalLogins,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IExternalAuthenticationHooks hooks,
        TimeProvider timeProvider,
        ILogger<ExternalAuthenticationFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(externalLogins);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.registry = registry;
        this.externalLogins = externalLogins;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.hooks = hooks;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginFlowResult> AuthenticateAsync(string provider, ExternalAuthRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (!registry.TryGet(provider, out var authProvider))
        {
            return await FailAsync(provider, ExternalLoginOutcome.ProviderNotFound, ExternalLoginFlowResult.ProviderNotFound(), cancellationToken).ConfigureAwait(false);
        }

        var before = new BeforeExternalLoginContext(provider);
        await hooks.OnBeforeExternalLoginAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return await FailAsync(provider, ExternalLoginOutcome.Denied, ExternalLoginFlowResult.Denied(), cancellationToken, before.DenialReason).ConfigureAwait(false);
        }

        var exchange = await authProvider.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!exchange.Succeeded || exchange.Identity is not { } identity)
        {
            return await FailAsync(provider, ExternalLoginOutcome.ProviderRejected, ExternalLoginFlowResult.ProviderRejected(), cancellationToken, exchange.FailureReason).ConfigureAwait(false);
        }

        var resolution = await externalLogins.ResolveOrProvisionAsync(identity, cancellationToken).ConfigureAwait(false);

        // Gate on the same active + lockout semantics the password flow enforces
        // (IUserService.VerifyPasswordAsync). A freshly-provisioned user is active by construction, so
        // this only blocks a pre-existing linked account that was deactivated or locked after linking —
        // closing the auth-bypass where such a user could still mint tokens via external login.
        if (!IsActiveAndUnlocked(resolution.User))
        {
            return await FailAsync(provider, ExternalLoginOutcome.AccountInactive, ExternalLoginFlowResult.AccountInactive(), cancellationToken).ConfigureAwait(false);
        }

        var succeeded = new ExternalLoginSucceededContext(resolution.User, resolution.WasCreated, resolution.WasLinked);
        await hooks.OnExternalLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return await FailAsync(provider, ExternalLoginOutcome.Denied, ExternalLoginFlowResult.Denied(), cancellationToken, succeeded.DenialReason).ConfigureAwait(false);
        }

        // Token issuance is the last step. For a freshly-provisioned user the user+link were already
        // committed by ResolveOrProvisionAsync, so a transient failure here returns a 500 but leaves a
        // resolvable link: the next attempt finds it and issues tokens without re-provisioning. The
        // success hook above means "authentication authorized" (it may still deny), not "session created".
        var tokens = await AuthTokenIssuer
            .IssueAsync(principalFactory, accessTokens, refreshTokens, timeProvider, resolution.User, AuthenticationType, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("User {UserId} authenticated via external provider {Provider}.", resolution.User.Id, provider);
        return ExternalLoginFlowResult.Success(tokens, resolution.WasCreated, resolution.WasLinked);
    }

    private async Task<ExternalLoginFlowResult> FailAsync(string provider, ExternalLoginOutcome reason, ExternalLoginFlowResult result, CancellationToken cancellationToken, string? denialReason = null)
    {
        if (denialReason is null)
        {
            logger.LogWarning("External login failed for provider {Provider}: {Reason}.", provider, reason);
        }
        else
        {
            logger.LogWarning("External login failed for provider {Provider}: {Reason} ({DenialReason}).", provider, reason, denialReason);
        }

        await hooks.OnExternalLoginFailedAsync(new ExternalLoginFailedContext(provider, reason), cancellationToken).ConfigureAwait(false);
        return result;
    }

    // Same inactive + lockout semantics as IUserService.VerifyPasswordAsync, via the shared
    // UserLockoutExtensions predicate so the two paths cannot drift.
    private bool IsActiveAndUnlocked(User user) =>
        user.IsActive && !user.IsLockedOut(timeProvider.GetUtcNow());
}
