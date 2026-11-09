using Fido2NetLib;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>How a bridge call ended.</summary>
/// <param name="Verification">What the second-factor service said, or null when the call was refused before it ran.</param>
/// <param name="Completion">The completed login, or null when nothing was issued.</param>
public sealed record SecondFactorBridgeResult(SecondFactorResult? Verification, SecondFactorCompletionResult? Completion)
{
    /// <summary>Whether the login completed and tokens were issued.</summary>
    public bool Succeeded => Completion is { Succeeded: true };
}

/// <summary>
/// Finishes a pending login (a <see cref="PendingSecondFactor"/>) by proving a second factor and, only when that succeeds, completing
/// the ticket with the proof recorded on the new session. Every method here is a real proof of possession: there is deliberately no
/// method for <see cref="ISecondFactorService.EnrolOtpAsync"/>, because enrolling an emailed or texted code never asks the user to
/// enter it and so verifies nothing. A failed proof issues nothing and leaves the ticket usable until it expires.
/// </summary>
public interface ISecondFactorBridge
{
    /// <summary>Verifies an authenticator-app code for a ticket in the <see cref="SecondFactorStage.Verify"/> stage.</summary>
    Task<SecondFactorBridgeResult> VerifyTotpAsync(PendingSecondFactor pending, Guid methodId, string code, CancellationToken cancellationToken = default);

    /// <summary>Verifies a recovery code for a ticket in the Verify stage. The session it opens may only enrol a strong method.</summary>
    Task<SecondFactorBridgeResult> VerifyRecoveryCodeAsync(PendingSecondFactor pending, string code, CancellationToken cancellationToken = default);

    /// <summary>Verifies an emailed or texted code for a ticket in the Verify stage.</summary>
    Task<SecondFactorBridgeResult> VerifyOtpAsync(PendingSecondFactor pending, MfaMethodKind kind, string code, CancellationToken cancellationToken = default);

    /// <summary>Verifies a passkey assertion for a ticket in the Verify stage.</summary>
    Task<SecondFactorBridgeResult> VerifyPasskeyAsync(PendingSecondFactor pending, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default);

    /// <summary>Confirms the first authenticator-app enrolment of a ticket in the <see cref="SecondFactorStage.Enrol"/> stage; the
    /// confirming code is the proof.</summary>
    Task<SecondFactorBridgeResult> ConfirmTotpEnrolAsync(PendingSecondFactor pending, string enrolmentId, string code, string label, CancellationToken cancellationToken = default);

    /// <summary>Completes the first passkey enrolment of a ticket in the Enrol stage; the attestation is the proof.</summary>
    Task<SecondFactorBridgeResult> CompletePasskeyEnrolAsync(PendingSecondFactor pending, AuthenticatorAttestationRawResponse response, string label, CancellationToken cancellationToken = default);
}

internal sealed class SecondFactorBridge(
    ISecondFactorService service,
    ISecondFactorSubjectResolver subjects,
    ISecondFactorTicketService tickets,
    ISecondFactorProofCompletion completion) : ISecondFactorBridge
{
    private delegate Task<SecondFactorResult> Prove(MfaSubject subject, MfaPrimary primary);

    public Task<SecondFactorBridgeResult> VerifyTotpAsync(PendingSecondFactor pending, Guid methodId, string code, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Verify, (s, p) => service.VerifyTotpAsync(s, p, methodId, code, cancellationToken), cancellationToken);

    public Task<SecondFactorBridgeResult> VerifyRecoveryCodeAsync(PendingSecondFactor pending, string code, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Verify, (s, p) => service.VerifyRecoveryCodeAsync(s, p, code, cancellationToken), cancellationToken);

    public Task<SecondFactorBridgeResult> VerifyOtpAsync(PendingSecondFactor pending, MfaMethodKind kind, string code, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Verify, (s, p) => service.VerifyOtpAsync(s, p, kind, code, cancellationToken), cancellationToken);

    public Task<SecondFactorBridgeResult> VerifyPasskeyAsync(PendingSecondFactor pending, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Verify, (s, p) => service.VerifyPasskeyAsync(s, p, response, cancellationToken), cancellationToken);

    public Task<SecondFactorBridgeResult> ConfirmTotpEnrolAsync(PendingSecondFactor pending, string enrolmentId, string code, string label, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Enrol, (s, _) => service.ConfirmTotpEnrolAsync(s, enrolmentId, code, label, null, cancellationToken), cancellationToken);

    public Task<SecondFactorBridgeResult> CompletePasskeyEnrolAsync(PendingSecondFactor pending, AuthenticatorAttestationRawResponse response, string label, CancellationToken cancellationToken = default) =>
        RunAsync(pending, SecondFactorStage.Enrol, (s, _) => service.CompletePasskeyEnrolAsync(s, response, label, null, cancellationToken), cancellationToken);

    private async Task<SecondFactorBridgeResult> RunAsync(PendingSecondFactor pending, SecondFactorStage stage, Prove prove, CancellationToken cancellationToken)
    {
        if (pending.Stage != stage)
        {
            return Refused();
        }

        // Only the signature and expiry are read here to learn the primary check; completing the ticket below is what burns it.
        var data = await tickets.PeekAsync(pending.Ticket, cancellationToken).ConfigureAwait(false);
        if (data is null || data.UserId != pending.UserId || data.Stage != stage)
        {
            return Refused();
        }

        // An app-channel ticket has no strong factor of its own to prove, and the completion refuses it only after the proof ran.
        if (data.Method == SecondFactorMethod.Custom)
        {
            return Refused();
        }

        // The request completing a ticket has no tenant scope under a claims-only strategy: run as the ticket's tenant.
        using (TenantContextAccessor.BeginScope(data.TenantId))
        {
            return await RunScopedAsync(pending, stage, data, prove, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SecondFactorBridgeResult> RunScopedAsync(
        PendingSecondFactor pending, SecondFactorStage stage, SecondFactorTicketData data, Prove prove, CancellationToken cancellationToken)
    {
        // The completion re-checks the stamp, but only after the proof ran; refuse a stale ticket before it can enrol anything.
        var stamp = await subjects.GetSecurityStampAsync(data.UserId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stamp, data.SecurityStamp, StringComparison.Ordinal))
        {
            return Refused();
        }

        var subject = await subjects.ResolveAsync(data.UserId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            return Refused();
        }

        var primary = SecondFactorMapping.ToPrimary(data.Method, data.Provider);
        var verified = await prove(subject, primary).ConfigureAwait(false);
        if (!verified.Succeeded)
        {
            return new SecondFactorBridgeResult(verified, null);
        }

        var restriction = await RestrictionAsync(subject, primary, stage, verified, cancellationToken).ConfigureAwait(false);
        var snapshot = SessionProofReader.ToSnapshot(verified, restriction);
        var completed = await completion.CompleteAsync(pending.Ticket, pending.UserId, stage, snapshot, cancellationToken).ConfigureAwait(false);
        return new SecondFactorBridgeResult(verified, completed);
    }

    // A recovery code, or a user who must still enrol a stronger or spare method, gets a session that can enrol and do nothing else.
    private async Task<SessionRestriction> RestrictionAsync(
        MfaSubject subject, MfaPrimary primary, SecondFactorStage stage, SecondFactorResult verified, CancellationToken cancellationToken)
    {
        if (verified.UsedRecoveryCode)
        {
            return SessionRestriction.RecoveryOnly;
        }

        if (stage == SecondFactorStage.Enrol)
        {
            return SessionRestriction.None;
        }

        var requirement = await service.EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        return requirement.UpgradeMandatory || requirement.SpareMandatory ? SessionRestriction.UpgradeDue : SessionRestriction.None;
    }

    private static SecondFactorBridgeResult Refused() => new(null, SecondFactorCompletionResult.Refused());
}
