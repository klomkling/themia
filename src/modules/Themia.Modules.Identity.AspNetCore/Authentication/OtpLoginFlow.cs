using Microsoft.Extensions.Logging;
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
    private readonly IOtpChallengeService challenges;
    private readonly IOtpCodeSender sender;
    private readonly IPhoneNumberNormalizer phoneNormalizer;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IAuthenticationHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OtpLoginFlow> logger;

    /// <summary>Creates the flow.</summary>
    public OtpLoginFlow(
        IUserService users,
        IOtpChallengeService challenges,
        IOtpCodeSender sender,
        IPhoneNumberNormalizer phoneNormalizer,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IAuthenticationHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        TimeProvider timeProvider,
        ILogger<OtpLoginFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.challenges = challenges;
        this.sender = sender;
        this.phoneNormalizer = phoneNormalizer;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
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
        // whether the phone has an account. An unconfirmed phone is a claim, not proof of control, and an
        // inactive or locked-out account must not be able to start a login: all three become a decoy.
        var candidate = await users.FindByPhoneAsync(phoneNumber, cancellationToken).ConfigureAwait(false);
        var recipient = candidate is not null && CanLogIn(candidate) ? candidate : null;

        var issued = await challenges.IssueAsync(key, recipient?.Id, cancellationToken).ConfigureAwait(false);
        if (issued.Outcome == OtpIssueOutcome.Throttled)
        {
            logger.LogWarning("OTP start throttled for {Phone}.", Mask(key));
            return new OtpStartResult(OtpStartOutcome.Throttled, Guid.Empty, default);
        }

        if (recipient is not null)
        {
            await DeliverAsync(recipient, issued, cancellationToken).ConfigureAwait(false);
        }

        return new OtpStartResult(OtpStartOutcome.Issued, issued.ChallengeId, issued.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<LoginResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var check = await challenges.VerifyAsync(challengeId, code, cancellationToken).ConfigureAwait(false);
        var identifier = $"otp:{challengeId:N}";
        if (!check.Verified || check.UserId is not { } userId)
        {
            var locked = check.Outcome == OtpVerifyOutcome.LockedOut;
            return await FailAsync(
                identifier,
                locked ? LoginFailureReason.OtpAttemptsExceeded : LoginFailureReason.InvalidOtp,
                locked ? LoginResult.LockedOut() : LoginResult.InvalidCredentials(),
                cancellationToken).ConfigureAwait(false);
        }

        // The code is right. Everything below is the password flow's gate, re-evaluated NOW: the account may
        // have been deactivated, locked or lost its confirmed phone since the code was sent.
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

    private bool CanLogIn(User user) =>
        user.IsActive && user.PhoneNumberConfirmed && !user.IsLockedOut(timeProvider.GetUtcNow());

    private async Task DeliverAsync(User recipient, OtpIssueResult issued, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(
                new OtpDelivery(recipient.PhoneNumber ?? string.Empty, issued.Code!, issued.ExpiresAt, issued.ChallengeId),
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
