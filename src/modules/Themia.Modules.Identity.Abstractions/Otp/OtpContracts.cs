using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Whether <see cref="IOtpLoginFlow.StartAsync"/> issued a challenge.</summary>
public enum OtpStartOutcome
{
    /// <summary>A challenge was issued. Returned for an unknown phone too.</summary>
    Issued,

    /// <summary>A throttle (resend cooldown, start cap or failure cap) refused the start. Keyed on the
    /// phone alone, so it is identical for a phone with an account and one without.</summary>
    Throttled,
}

/// <summary>Result of <see cref="IOtpLoginFlow.StartAsync"/>.</summary>
/// <param name="Outcome">Issued or throttled.</param>
/// <param name="ChallengeId">Pass to <see cref="IOtpLoginFlow.VerifyAsync"/>; <see cref="Guid.Empty"/> when throttled.</param>
/// <param name="ExpiresAt">When the code stops being accepted; default when throttled.</param>
public readonly record struct OtpStartResult(OtpStartOutcome Outcome, Guid ChallengeId, DateTimeOffset ExpiresAt)
{
    /// <summary>Whether a challenge was issued.</summary>
    public bool Issued => Outcome == OtpStartOutcome.Issued;
}

/// <summary>
/// Passwordless phone-code login. <see cref="StartAsync"/> sends a short numeric code to a confirmed
/// phone; <see cref="VerifyAsync"/> exchanges it for the same session as password login.
/// </summary>
public interface IOtpLoginFlow
{
    /// <summary>
    /// Issues a challenge for <paramref name="phoneNumber"/> and hands the code to
    /// <see cref="IOtpCodeSender"/> when the phone belongs to an active user with a confirmed number. The
    /// result is the same shape whether or not it does.
    /// </summary>
    Task<OtpStartResult> StartAsync(string phoneNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks <paramref name="code"/> against the challenge and, when it is right, runs the login hooks and
    /// issues tokens exactly as <see cref="IAuthenticationFlow.LoginAsync"/> does. Failure is
    /// <see cref="LoginOutcome.InvalidCredentials"/> for a wrong, expired, used or unknown challenge,
    /// <see cref="LoginOutcome.LockedOut"/> once the attempt cap is hit, and
    /// <see cref="LoginOutcome.Denied"/> when a hook refuses.
    /// </summary>
    Task<LoginResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IOtpCodeSender"/> must deliver.</summary>
/// <param name="PhoneNumber">The phone number as the user is registered with it.</param>
/// <param name="Code">The plain code. Never log it.</param>
/// <param name="ExpiresAt">When the code stops being accepted.</param>
/// <param name="ChallengeId">The challenge the code belongs to (safe to log).</param>
public sealed record OtpDelivery(string PhoneNumber, string Code, DateTimeOffset ExpiresAt, Guid ChallengeId);

/// <summary>
/// Delivers a code (SMS, push, ...). Implemented by the application.
/// </summary>
/// <remarks>
/// <para>
/// Implementations should enqueue the message and return quickly. <see cref="IOtpLoginFlow.StartAsync"/>
/// awaits this call only for a phone that can log in, so a slow gateway makes the response measurably
/// slower for a real account than for an unknown phone. The start and failure throttles bound how often an
/// outsider can probe that difference, but they do not remove it.
/// </para>
/// <para>
/// The implementation must not write the code to logs, traces or exception messages. An exception thrown
/// here is logged without its message by the flow and the caller still gets the normal response.
/// </para>
/// </remarks>
public interface IOtpCodeSender
{
    /// <summary>Delivers the code.</summary>
    Task SendAsync(OtpDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>Whether <see cref="IOtpChallengeService.IssueAsync"/> issued a challenge.</summary>
public enum OtpIssueOutcome
{
    /// <summary>Issued.</summary>
    Issued,

    /// <summary>Refused by a throttle.</summary>
    Throttled,
}

/// <summary>Result of <see cref="IOtpChallengeService.IssueAsync"/>.</summary>
/// <param name="Outcome">Issued or throttled.</param>
/// <param name="ChallengeId">The new challenge; <see cref="Guid.Empty"/> when throttled.</param>
/// <param name="ExpiresAt">Expiry; default when throttled.</param>
/// <param name="Code">The plain code for the caller to deliver; <see langword="null"/> when throttled.</param>
public readonly record struct OtpIssueResult(OtpIssueOutcome Outcome, Guid ChallengeId, DateTimeOffset ExpiresAt, string? Code);

/// <summary>Whether a code was accepted.</summary>
public enum OtpVerifyOutcome
{
    /// <summary>The code was right; the challenge is now used.</summary>
    Verified,

    /// <summary>Wrong, expired, used, superseded or unknown.</summary>
    Invalid,

    /// <summary>The attempt cap (per challenge or per phone window) is exhausted.</summary>
    LockedOut,
}

/// <summary>Result of <see cref="IOtpChallengeService.VerifyAsync"/>.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="UserId">The user the challenge was issued for; set only when <see cref="OtpVerifyOutcome.Verified"/>.</param>
/// <param name="NormalizedPhoneNumber">The phone the challenge was issued for; set only when verified.</param>
public readonly record struct OtpVerifyResult(OtpVerifyOutcome Outcome, Guid? UserId, string? NormalizedPhoneNumber)
{
    /// <summary>The tenant the challenge was issued in (<see langword="null"/> for a platform user); set only
    /// when verified. The login flow runs as this tenant and requires the user to belong to it.</summary>
    public Themia.Framework.Core.Abstractions.Tenancy.TenantId? TenantId { get; init; }

    /// <summary>Whether the code was accepted.</summary>
    public bool Verified => Outcome == OtpVerifyOutcome.Verified;
}

/// <summary>
/// The challenge store and its rules: code generation, keyed hashing, expiry, the attempt cap, the
/// throttles. Knows nothing about users or tokens; <see cref="IOtpLoginFlow"/> does that.
/// </summary>
public interface IOtpChallengeService
{
    /// <summary>Issues a challenge for <paramref name="normalizedPhoneNumber"/>. A null
    /// <paramref name="userId"/> issues a decoy whose code must not be delivered.</summary>
    Task<OtpIssueResult> IssueAsync(string normalizedPhoneNumber, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Counts one guess against the challenge and compares it, in one conditional update.</summary>
    Task<OtpVerifyResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default);
}

/// <summary>Who a one-time code may be sent to: the single eligible user holding a phone number.</summary>
/// <param name="UserId">The user.</param>
/// <param name="TenantId">The user's tenant; <see langword="null"/> for a platform user.</param>
/// <param name="PhoneNumber">The number as the user is registered with it.</param>
public sealed record OtpRecipient(Guid UserId, Themia.Framework.Core.Abstractions.Tenancy.TenantId? TenantId, string PhoneNumber);

/// <summary>
/// Finds the one user a phone number belongs to, across tenants, for <see cref="IOtpLoginFlow.StartAsync"/>.
/// A start request carries no JWT, so under a claims-only tenant strategy the ambient scope is null and a
/// scoped lookup would never find a tenant user.
/// </summary>
public interface IOtpRecipientResolver
{
    /// <summary>
    /// Returns the recipient when exactly one active user with a confirmed phone and no lockout holds
    /// <paramref name="normalizedPhoneNumber"/>; <see langword="null"/> for none or for several (ambiguous,
    /// fail closed). When an ambient tenant exists, only that tenant's users and platform users are
    /// candidates. Returns identifiers only, never the user.
    /// </summary>
    Task<OtpRecipient?> ResolveAsync(string normalizedPhoneNumber, CancellationToken cancellationToken = default);
}
