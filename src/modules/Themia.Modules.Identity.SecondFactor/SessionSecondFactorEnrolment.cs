using Fido2NetLib;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>
/// Enrols a strong second factor from an already signed-in session and lifts that session's restriction once it succeeded. The
/// session is the one named by the <c>sid</c> claim; the proof is read from the server, never from claims. A failed or weak
/// enrolment leaves the restriction in place.
/// </summary>
public interface ISessionSecondFactorEnrolment
{
    /// <summary>Confirms an authenticator-app enrolment begun with <see cref="ISecondFactorService.BeginTotpEnrolAsync"/>.</summary>
    Task<SecondFactorResult> ConfirmTotpEnrolAsync(Guid userId, Guid sessionId, string enrolmentId, string code, string label, CancellationToken cancellationToken = default);

    /// <summary>Completes a passkey enrolment begun with <see cref="ISecondFactorService.BeginPasskeyEnrolAsync"/>.</summary>
    Task<SecondFactorResult> CompletePasskeyEnrolAsync(Guid userId, Guid sessionId, AuthenticatorAttestationRawResponse response, string label, CancellationToken cancellationToken = default);
}

internal sealed class SessionSecondFactorEnrolment(
    ISecondFactorService service,
    ISecondFactorSubjectResolver subjects,
    ISessionProofReader proofs,
    ISessionProofStore store) : ISessionSecondFactorEnrolment
{
    public Task<SecondFactorResult> ConfirmTotpEnrolAsync(Guid userId, Guid sessionId, string enrolmentId, string code, string label, CancellationToken cancellationToken = default) =>
        EnrolAsync(userId, sessionId, (subject, proof) => service.ConfirmTotpEnrolAsync(subject, enrolmentId, code, label, proof, cancellationToken), cancellationToken);

    public Task<SecondFactorResult> CompletePasskeyEnrolAsync(Guid userId, Guid sessionId, AuthenticatorAttestationRawResponse response, string label, CancellationToken cancellationToken = default) =>
        EnrolAsync(userId, sessionId, (subject, proof) => service.CompletePasskeyEnrolAsync(subject, response, label, proof, cancellationToken), cancellationToken);

    private async Task<SecondFactorResult> EnrolAsync(
        Guid userId, Guid sessionId, Func<MfaSubject, MfaProof?, Task<SecondFactorResult>> enrol, CancellationToken cancellationToken)
    {
        var subject = await subjects.ResolveAsync(userId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            return new SecondFactorResult { Outcome = SecondFactorOutcome.NotAvailable };
        }

        var proof = await proofs.GetAsync(userId, sessionId, cancellationToken).ConfigureAwait(false);
        var result = await enrol(subject, proof).ConfigureAwait(false);
        if (result.Succeeded && result.Strength == MfaStrength.Strong)
        {
            await store.LiftRestrictionAsync(userId, sessionId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
