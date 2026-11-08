namespace Themia.SecondFactor;

/// <summary>Protects TOTP secrets at rest. The adopter wires this to Data Protection or a KMS.</summary>
public interface ISecondFactorSecretProtector
{
    /// <summary>The scheme name stored beside every secret this protector writes.</summary>
    string Scheme { get; }

    /// <summary>Protects <paramref name="secret"/> for storage.</summary>
    string Protect(string secret);

    /// <summary>
    /// Reads a stored secret. Returns <see langword="null"/> for a scheme this protector does not know, so verification
    /// fails closed instead of accepting anything.
    /// </summary>
    string? Unprotect(string scheme, string protectedSecret);
}

/// <summary>Where the user can be reached, and how to name them in an authenticator app.</summary>
/// <param name="AccountLabel">The email or username shown in the authenticator app. Never the user id.</param>
/// <param name="Email">The verified email, or <see langword="null"/>.</param>
/// <param name="Phone">The verified phone number, or <see langword="null"/>.</param>
public sealed record MfaContact(string AccountLabel, string? Email, string? Phone);

/// <summary>Resolves contact details live from the app, so a changed email or phone never leaves a stale destination.</summary>
public interface ISecondFactorContactResolver
{
    /// <summary>The user's contact details, or <see langword="null"/> when the user is unknown.</summary>
    Task<MfaContact?> ResolveAsync(MfaSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>Sends a one-time code over one channel. Implemented by the adopter so the sender stays swappable (SES, Sms2Pro, ...).</summary>
public interface IOtpDelivery
{
    /// <summary>The method kind this delivery serves: <see cref="MfaMethodKind.EmailOtp"/> or <see cref="MfaMethodKind.SmsOtp"/>.</summary>
    MfaMethodKind Kind { get; }

    /// <summary>Sends <paramref name="code"/> to <paramref name="destination"/>. Must not log the destination.</summary>
    Task SendAsync(string destination, string code, CancellationToken cancellationToken = default);
}

/// <summary>Proof the caller already holds from this session, read from the server-side session and never from a client claim.</summary>
/// <param name="Strength">The strongest method verified.</param>
/// <param name="AuthenticatedAt">When it was verified.</param>
public sealed record MfaProof(MfaStrength Strength, DateTimeOffset AuthenticatedAt);

/// <summary>What happened to a verify, enrol or remove request.</summary>
public enum SecondFactorOutcome
{
    /// <summary>The request succeeded.</summary>
    Succeeded,

    /// <summary>The code, assertion or recovery code was wrong.</summary>
    Incorrect,

    /// <summary>Too many failures; try again after the lock ends.</summary>
    Locked,

    /// <summary>The method is not enrolled, not offered, or not allowed for this primary.</summary>
    NotAvailable,

    /// <summary>The action needs a fresh strong verification the session does not have.</summary>
    StepUpRequired,

    /// <summary>The action is not allowed (would drop the last method, wrong state).</summary>
    NotAllowed,

    /// <summary>The ceremony or enrolment was unknown, expired, or already used.</summary>
    CeremonyExpired,

    /// <summary>The passkey credential is already bound to an account.</summary>
    DuplicateCredential,

    /// <summary>A passkey assertion was valid but the counter went backwards (cloned authenticator).</summary>
    CounterRegressed,

    /// <summary>The code was already used (TOTP replay).</summary>
    Replayed,

    /// <summary>The one-time code could not be issued (rate limited, no contact, delivery failed).</summary>
    CannotIssue,
}

/// <summary>The result of a verify, enrol or remove request.</summary>
public sealed record SecondFactorResult
{
    /// <summary>What happened.</summary>
    public required SecondFactorOutcome Outcome { get; init; }

    /// <summary>The method that was used, when one was.</summary>
    public MfaMethodKind? Kind { get; init; }

    /// <summary>Whether a recovery code was the method.</summary>
    public bool UsedRecoveryCode { get; init; }

    /// <summary>The strength proven. Meaningful only when <see cref="Succeeded"/>.</summary>
    public MfaStrength Strength { get; init; }

    /// <summary>When it was proven.</summary>
    public DateTimeOffset? AuthenticatedAt { get; init; }

    /// <summary>The amr values to carry into the session and any federated assertion.</summary>
    public IReadOnlyList<string> Amr { get; init; } = [];

    /// <summary>When a lock applies, the earliest time to try again.</summary>
    public DateTimeOffset? LockedUntil { get; init; }

    /// <summary>Whether the request succeeded.</summary>
    public bool Succeeded => Outcome == SecondFactorOutcome.Succeeded;

    internal static SecondFactorResult Failed(SecondFactorOutcome outcome, DateTimeOffset? lockedUntil = null) =>
        new() { Outcome = outcome, LockedUntil = lockedUntil };
}

/// <summary>A TOTP enrolment in progress. Show <see cref="QrSvg"/> and the secret, then confirm with a code.</summary>
/// <param name="EnrolmentId">Pass back to confirm.</param>
/// <param name="Secret">The base32 secret, for manual entry.</param>
/// <param name="OtpAuthUri">The otpauth URI the QR encodes.</param>
/// <param name="QrSvg">The QR code as an SVG string, rendered server-side.</param>
public sealed record TotpEnrolment(string EnrolmentId, string Secret, Uri OtpAuthUri, string QrSvg);

/// <summary>A passkey ceremony to hand to the browser.</summary>
/// <param name="OptionsJson">The WebAuthn options as JSON (<c>navigator.credentials.create/get</c> input).</param>
public sealed record PasskeyCeremony(string OptionsJson);

/// <summary>A started one-time-code challenge.</summary>
/// <param name="Outcome"><see cref="SecondFactorOutcome.Succeeded"/> when the code was sent.</param>
public sealed record OtpChallengeResult(SecondFactorOutcome Outcome)
{
    /// <summary>Whether the code was sent.</summary>
    public bool Succeeded => Outcome == SecondFactorOutcome.Succeeded;
}

/// <summary>New recovery codes. Shown once; only their hashes are stored.</summary>
/// <param name="Outcome">Whether the codes were minted.</param>
/// <param name="Codes">The plain codes, or empty when not minted.</param>
public sealed record RecoveryCodesResult(SecondFactorOutcome Outcome, IReadOnlyList<string> Codes)
{
    /// <summary>Whether the codes were minted.</summary>
    public bool Succeeded => Outcome == SecondFactorOutcome.Succeeded;
}
