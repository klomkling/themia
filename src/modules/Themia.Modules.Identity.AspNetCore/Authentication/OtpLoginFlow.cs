using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Default <see cref="IOtpLoginFlow"/>. The challenge rules (hashing, attempt cap, throttles) live in
/// <see cref="IOtpChallengeService"/>; this class decides who may receive a code and, once a code is
/// right, applies the same account checks, hooks, observers and token issue as
/// <see cref="AuthenticationFlow"/> so the resulting session is indistinguishable from a password login.
/// </summary>
public sealed class OtpLoginFlow : IOtpLoginFlow
{
    private const string AuthenticationType = "Bearer";

    private readonly IUserService users;
    private readonly IOtpRecipientResolver recipients;
    private readonly IOtpChallengeService challenges;
    private readonly IOtpCodeSender sender;
    private readonly IPhoneNumberNormalizer phoneNormalizer;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IAuthenticationHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly IdentityModuleOptions identityOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OtpLoginFlow> logger;

    /// <summary>Creates the flow.</summary>
    public OtpLoginFlow(
        IUserService users,
        IOtpRecipientResolver recipients,
        IOtpChallengeService challenges,
        IOtpCodeSender sender,
        IPhoneNumberNormalizer phoneNormalizer,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IAuthenticationHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        IdentityModuleOptions identityOptions,
        TimeProvider timeProvider,
        ILogger<OtpLoginFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(identityOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.recipients = recipients;
        this.challenges = challenges;
        this.sender = sender;
        this.phoneNormalizer = phoneNormalizer;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        this.identityOptions = identityOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<OtpStartResult> StartAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);

        // Keyed on the normalized number (or the trimmed input when it will not normalize), so formatting
        // variants share one throttle budget and an unparseable number is throttled like any other.
        var key = phoneNormalizer.Normalize(phoneNumber) ?? phoneNumber.Trim();

        // Looked up before the throttle check and whatever its answer, so the work done does not depend on
        // whether the phone has an account. The resolver looks across tenants (a start request has no JWT,
        // so under a claims-only strategy the ambient scope is null) and answers only for exactly one
        // active, confirmed, unlocked holder; no match, an unconfirmed or locked holder and an ambiguous
        // number all become the same decoy.
        // With one-time-code login switched off, no phone has a recipient: every start takes the decoy path
        // below, so it answers like a phone with no account and no code is ever sent.
        var recipient = identityOptions.EnabledLoginMethods.HasFlag(LoginMethods.Otp)
            ? await recipients.ResolveAsync(key, cancellationToken).ConfigureAwait(false)
            : null;

        OtpIssueResult issued;
        if (recipient is null)
        {
            issued = await challenges.IssueAsync(key, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // As the recipient's tenant, so the challenge row is stamped with it and the sender runs in it.
            using (TenantContextAccessor.BeginScope(recipient.TenantId))
            {
                issued = await challenges.IssueAsync(key, recipient.UserId, cancellationToken).ConfigureAwait(false);
                if (issued.Outcome == OtpIssueOutcome.Issued)
                {
                    await DeliverAsync(recipient, issued, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (issued.Outcome == OtpIssueOutcome.Throttled)
        {
            logger.LogWarning("OTP start throttled for {Phone}.", Mask(key));
            return new OtpStartResult(OtpStartOutcome.Throttled, Guid.Empty, default);
        }

        return new OtpStartResult(OtpStartOutcome.Issued, issued.ChallengeId, issued.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<LoginResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var identifier = $"otp:{challengeId:N}";
        if (!identityOptions.EnabledLoginMethods.HasFlag(LoginMethods.Otp))
        {
            // Refused like a wrong code, before the challenge is read, so a code issued before the method was
            // switched off cannot be redeemed and no guess is counted against it.
            return await FailAsync(identifier, LoginFailureReason.InvalidOtp, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        var check = await challenges.VerifyAsync(challengeId, code, cancellationToken).ConfigureAwait(false);
        if (!check.Verified || check.UserId is not { } userId)
        {
            var locked = check.Outcome == OtpVerifyOutcome.LockedOut;
            return await FailAsync(
                identifier,
                locked ? LoginFailureReason.OtpAttemptsExceeded : LoginFailureReason.InvalidOtp,
                locked ? LoginResult.LockedOut() : LoginResult.InvalidCredentials(),
                cancellationToken).ConfigureAwait(false);
        }

        // The code is right, and it was proved against a challenge issued in a specific tenant. The request
        // carries no tenant scope under a claims-only strategy, so everything from here runs as the
        // challenge's own tenant, and the user must belong to that tenant.
        using (TenantContextAccessor.BeginScope(check.TenantId))
        {
            return await CompleteVerifyAsync(check, userId, identifier, challengeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LoginResult> CompleteVerifyAsync(
        OtpVerifyResult check, Guid userId, string identifier, Guid challengeId, CancellationToken cancellationToken)
    {
        // The password flow's gate, re-evaluated NOW: the account may have been deactivated, locked or lost its
        // confirmed phone since the code was sent.
        var phone = check.NormalizedPhoneNumber ?? identifier;
        var before = new BeforeLoginContext(phone);
        await hooks.OnBeforeLoginAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return await FailAsync(phone, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, before.DenialReason).ConfigureAwait(false);
        }

        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailAsync(phone, LoginFailureReason.NotFound, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        // Re-checked here as well as in the resolver: a challenge issued before platform login was switched off
        // must not be redeemable for a platform user afterwards.
        if (user.TenantId is null && !identityOptions.AllowPlatformLogin)
        {
            return await FailAsync(phone, LoginFailureReason.InvalidOtp, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        // A challenge can only ever log in a user of its own tenant.
        if (!Equals(user.TenantId, check.TenantId))
        {
            return await FailAsync(phone, LoginFailureReason.InvalidOtp, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        if (!user.IsActive)
        {
            return await FailAsync(phone, LoginFailureReason.Inactive, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        if (user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return await FailAsync(phone, LoginFailureReason.LockedOut, LoginResult.LockedOut(), cancellationToken).ConfigureAwait(false);
        }

        // The code proves control of the number it was sent to. If the account no longer holds that number
        // (changed or removed since the code went out), the proof is for someone else's number.
        if (!user.PhoneNumberConfirmed || !string.Equals(user.NormalizedPhoneNumber, check.NormalizedPhoneNumber, StringComparison.Ordinal))
        {
            return await FailAsync(phone, LoginFailureReason.InvalidOtp, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        var succeeded = new LoginSucceededContext(user);
        await hooks.OnLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return await FailAsync(phone, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken, succeeded.DenialReason).ConfigureAwait(false);
        }

        var tokens = await AuthTokenIssuer.IssueAsync(
            principalFactory, accessTokens, refreshTokens, timeProvider, user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("User {UserId} authenticated via one-time phone code (challenge {ChallengeId}).", user.Id, challengeId);
        await RaiseAsync((o, ct) => o.OnLoginSucceededAsync(user.Id, user.UserName, ct), cancellationToken).ConfigureAwait(false);
        return LoginResult.Success(tokens);
    }

    private async Task DeliverAsync(OtpRecipient recipient, OtpIssueResult issued, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(
                new OtpDelivery(recipient.PhoneNumber, issued.Code!, issued.ExpiresAt, issued.ChallengeId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed on purpose: a failure here must not make the response differ from the unknown-phone
            // case. Only the type is logged: the sender may have put the code or the number in the message.
            logger.LogError("Delivering the one-time code for challenge {ChallengeId} failed ({ExceptionType}).", issued.ChallengeId, ex.GetType().Name);
        }
    }

    private async Task<LoginResult> FailAsync(string identifier, LoginFailureReason reason, LoginResult result, CancellationToken cancellationToken, string? denialReason = null)
    {
        if (denialReason is null)
        {
            logger.LogWarning("One-time code login failed for {Identifier}: {Reason}.", Mask(identifier), reason);
        }
        else
        {
            logger.LogWarning("One-time code login failed for {Identifier}: {Reason} ({DenialReason}).", Mask(identifier), reason, denialReason);
        }

        await hooks.OnLoginFailedAsync(new LoginFailedContext(identifier, reason), cancellationToken).ConfigureAwait(false);
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

    private static string Mask(string value) =>
        value.Length <= 4 ? "****" : new string('*', value.Length - 4) + value[^4..];
}
