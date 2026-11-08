using Fido2NetLib;

namespace Themia.SecondFactor;

/// <summary>A value plus the outcome of the request that produced it.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Outcome">What happened.</param>
/// <param name="Value">The value, present only when <see cref="Succeeded"/>.</param>
public sealed record SecondFactorValue<T>(SecondFactorOutcome Outcome, T? Value = default)
    where T : class
{
    /// <summary>Whether the request succeeded.</summary>
    public bool Succeeded => Outcome == SecondFactorOutcome.Succeeded;
}

/// <summary>
/// The second-factor flows: pick, challenge and verify at sign-in, and enrol, remove and mint recovery codes inside a
/// session. Authorization (step-up, upgrade path, minimums) is enforced here; the caller passes the <see cref="MfaProof"/>
/// it holds from the server-side session, never one read from a client claim.
/// </summary>
public interface ISecondFactorService
{
    /// <summary>The user's methods, disabled ones included.</summary>
    Task<IReadOnlyList<MfaMethod>> ListMethodsAsync(MfaSubject subject, CancellationToken cancellationToken = default);

    /// <summary>What this sign-in must do next. Starts the spare-method grace clock when it first applies.</summary>
    Task<MfaRequirement> EvaluateAsync(MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default);

    /// <summary>Verifies an authenticator-app code against one enrolled TOTP method.</summary>
    Task<SecondFactorResult> VerifyTotpAsync(MfaSubject subject, MfaPrimary primary, Guid methodId, string code, CancellationToken cancellationToken = default);

    /// <summary>Verifies a recovery code (strong, single use).</summary>
    Task<SecondFactorResult> VerifyRecoveryCodeAsync(MfaSubject subject, MfaPrimary primary, string code, CancellationToken cancellationToken = default);

    /// <summary>Sends a one-time code over the email or SMS method.</summary>
    Task<OtpChallengeResult> BeginOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, CancellationToken cancellationToken = default);

    /// <summary>Verifies the emailed or texted code.</summary>
    Task<SecondFactorResult> VerifyOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, string code, CancellationToken cancellationToken = default);

    /// <summary>Starts a passkey sign-in for the user's passkeys.</summary>
    Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyAssertionAsync(MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default);

    /// <summary>Verifies a passkey assertion.</summary>
    Task<SecondFactorResult> VerifyPasskeyAsync(MfaSubject subject, MfaPrimary primary, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default);

    /// <summary>Starts a TOTP enrolment: a fresh secret, its otpauth URI (labelled with the email/username) and a QR SVG.</summary>
    Task<SecondFactorValue<TotpEnrolment>> BeginTotpEnrolAsync(MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default);

    /// <summary>Confirms a TOTP enrolment with a code from the app. The confirming code is spent and cannot sign in.</summary>
    Task<SecondFactorResult> ConfirmTotpEnrolAsync(MfaSubject subject, string enrolmentId, string code, string label, CancellationToken cancellationToken = default);

    /// <summary>Starts a passkey registration.</summary>
    Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyEnrolAsync(MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default);

    /// <summary>Completes a passkey registration.</summary>
    Task<SecondFactorResult> CompletePasskeyEnrolAsync(MfaSubject subject, AuthenticatorAttestationRawResponse response, string label, CancellationToken cancellationToken = default);

    /// <summary>Enrols the user's verified email or phone as a one-time-code method.</summary>
    Task<SecondFactorResult> EnrolOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, MfaProof? proof, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the user's methods. Needs a fresh strong proof and must not leave the user below policy.</summary>
    Task<SecondFactorResult> RemoveMethodAsync(MfaSubject subject, MfaPrimary primary, Guid methodId, MfaProof? proof, CancellationToken cancellationToken = default);

    /// <summary>Mints a fresh batch of recovery codes (replacing unused ones). Needs a fresh strong proof.</summary>
    Task<RecoveryCodesResult> GenerateRecoveryCodesAsync(MfaSubject subject, MfaProof? proof, CancellationToken cancellationToken = default);
}
