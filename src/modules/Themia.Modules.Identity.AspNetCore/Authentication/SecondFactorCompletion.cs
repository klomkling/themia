using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Default <see cref="ISecondFactorCompletion"/>. Redeems the ticket, then repeats the gates of the flow that issued
/// it, in the ticket's tenant scope, because the user, the module options or the hooks may have changed since the
/// primary check: the user must still exist in that tenant, be active and not locked out, hold the same security
/// stamp, and the login method must still be switched on. The method's own success hook and observer then run
/// exactly as they would have, and the session is issued through the same issuer as every other login (so
/// <c>sid</c> and the claims are identical).
/// </summary>
internal sealed class SecondFactorCompletion : ISecondFactorCompletion
{
    private const string AuthenticationType = "Bearer";

    private readonly ISecondFactorTicketService tickets;
    private readonly IUserService users;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IAuthenticationHooks hooks;
    private readonly IExternalAuthenticationHooks? externalHooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly IdentityModuleOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SecondFactorCompletion> logger;

    public SecondFactorCompletion(
        ISecondFactorTicketService tickets,
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IAuthenticationHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        IdentityModuleOptions options,
        TimeProvider timeProvider,
        ILogger<SecondFactorCompletion> logger,
        IExternalAuthenticationHooks? externalHooks = null)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.tickets = tickets;
        this.users = users;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.hooks = hooks;
        this.externalHooks = externalHooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<SecondFactorCompletionResult> CompleteAsync(
        string ticket, Guid expectedUserId, SecondFactorStage expectedStage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);

        var data = await tickets.ConsumeAsync(ticket, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            logger.LogWarning("Second-factor completion refused: the ticket is unknown, expired, tampered with or already used.");
            return SecondFactorCompletionResult.Refused();
        }

        // The ticket is burnt by now. It must be for the user and stage the app just handled: otherwise an attacker holding a
        // victim's ticket could pass their own factor at a verify endpoint, or spend a verify ticket at an enrol endpoint.
        if (data.UserId != expectedUserId || data.Stage != expectedStage)
        {
            logger.LogWarning("Second-factor completion refused: the ticket is not for the user or stage the caller verified.");
            return SecondFactorCompletionResult.Refused();
        }

        // The completing request has no tenant scope under a claims-only strategy: run as the ticket's tenant.
        using (TenantContextAccessor.BeginScope(data.TenantId))
        {
            return await CompleteScopedAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task RejectAsync(string ticket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        return tickets.ConsumeAsync(ticket, cancellationToken);
    }

    private async Task<SecondFactorCompletionResult> CompleteScopedAsync(SecondFactorTicketData data, CancellationToken cancellationToken)
    {
        if (!options.EnabledLoginMethods.HasFlag(ToFlag(data.Method)))
        {
            logger.LogWarning("Second-factor completion refused for user {UserId}: the {Method} login method has been switched off since the ticket was issued.", data.UserId, data.Method);
            return SecondFactorCompletionResult.Refused();
        }

        var user = await users.FindByIdAsync(data.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !StillMayLogIn(user, data))
        {
            logger.LogWarning("Second-factor completion refused for user {UserId}: the account can no longer sign in as it was when the ticket was issued.", data.UserId);
            return SecondFactorCompletionResult.Refused();
        }

        if (data.Method == SecondFactorMethod.External)
        {
            // Without the external-login package there is no external hook to run, and no external flow that could have issued this ticket.
            if (externalHooks is null)
            {
                logger.LogWarning("Second-factor completion refused for user {UserId}: an external-login ticket without external login registered.", user.Id);
                return SecondFactorCompletionResult.Refused();
            }

            var succeeded = new ExternalLoginSucceededContext(user, data.WasCreated, data.WasLinked);
            await externalHooks.OnExternalLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
            if (succeeded.IsDenied)
            {
                return await DenyExternalAsync(data.Provider ?? string.Empty, succeeded.DenialReason, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var succeeded = new LoginSucceededContext(user);
            await hooks.OnLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
            if (succeeded.IsDenied)
            {
                return await DenyAsync(user.UserName, succeeded.DenialReason, cancellationToken).ConfigureAwait(false);
            }
        }

        var tokens = await AuthTokenIssuer.IssueAsync(
            principalFactory, accessTokens, refreshTokens, timeProvider, user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("User {UserId} completed the second factor ({Method}).", user.Id, data.Method);

        if (data.Method == SecondFactorMethod.External)
        {
            await RaiseAsync((o, ct) => o.OnExternalLoginSucceededAsync(user.Id, data.Provider ?? string.Empty, data.WasCreated, data.WasLinked, ct), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RaiseAsync((o, ct) => o.OnLoginSucceededAsync(user.Id, user.UserName, ct), cancellationToken).ConfigureAwait(false);
        }

        return SecondFactorCompletionResult.Completed(tokens, data.Method, data.Provider, data.WasCreated, data.WasLinked);
    }

    private bool StillMayLogIn(User user, SecondFactorTicketData data)
    {
        if (user.TenantId is null && !options.AllowPlatformLogin)
        {
            return false;
        }

        if (!Equals(user.TenantId, data.TenantId) || !user.IsActive || user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return false;
        }

        // A changed password, phone or other stamp-rotating mutation since the primary check voids the ticket.
        if (!string.Equals(user.SecurityStamp, data.SecurityStamp, StringComparison.Ordinal))
        {
            return false;
        }

        // The one-time code proved control of the phone; if the account no longer holds a confirmed number, it proved nothing.
        return data.Method != SecondFactorMethod.Otp || user.PhoneNumberConfirmed;
    }

    private async Task<SecondFactorCompletionResult> DenyAsync(string userName, string? denialReason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Second-factor completion denied by a hook ({DenialReason}).", denialReason);
        await hooks.OnLoginFailedAsync(new LoginFailedContext(userName, LoginFailureReason.Denied), cancellationToken).ConfigureAwait(false);
        await RaiseAsync((o, ct) => o.OnLoginDeniedAsync(userName, denialReason, ct), cancellationToken).ConfigureAwait(false);
        return SecondFactorCompletionResult.Denied();
    }

    private async Task<SecondFactorCompletionResult> DenyExternalAsync(string provider, string? denialReason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Second-factor completion for provider {Provider} denied by a hook ({DenialReason}).", provider, denialReason);
        await externalHooks!.OnExternalLoginFailedAsync(new ExternalLoginFailedContext(provider, ExternalLoginOutcome.Denied), cancellationToken).ConfigureAwait(false);
        await RaiseAsync((o, ct) => o.OnExternalLoginDeniedAsync(provider, denialReason, ct), cancellationToken).ConfigureAwait(false);
        return SecondFactorCompletionResult.Denied();
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

    private static LoginMethods ToFlag(SecondFactorMethod method) => method switch
    {
        SecondFactorMethod.Password => LoginMethods.Password,
        SecondFactorMethod.Otp => LoginMethods.Otp,
        SecondFactorMethod.External => LoginMethods.External,
        _ => LoginMethods.None,
    };
}
