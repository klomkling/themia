namespace Themia.SecondFactor;

/// <summary>What happened to an admin reset request.</summary>
public enum MfaAdminResetOutcome
{
    /// <summary>The target's methods, recovery codes and pending enrolments were removed and the reset was audited.</summary>
    Succeeded,

    /// <summary>The actor has no fresh strong verification. Ask them to verify again.</summary>
    StepUpRequired,

    /// <summary>The actor may not act on this target: themselves, another tenant, a higher level, an unknown user or a failed check.</summary>
    NotAllowed,

    /// <summary>The target id or the reason is blank, or the reason is too long.</summary>
    InvalidRequest,

    /// <summary>
    /// The reset was applied and audited, but the target's sessions could not all be revoked. Nothing was rolled back; the call is
    /// idempotent, so the admin can repeat it. Until it succeeds a live session of the target may still act.
    /// </summary>
    SessionsNotRevoked,
}

/// <summary>Decides whether an actor may reset another user's second factor. There is no default: the host supplies the rule.</summary>
public interface IMfaActOnPolicy
{
    /// <summary>
    /// <see langword="true"/> only when the target exists in the actor's tenant and the actor may act on them (for example a role-level
    /// check). Return <see langword="false"/> for anything unknown or unparsable: the caller treats an exception the same way.
    /// </summary>
    /// <param name="actor">The acting user.</param>
    /// <param name="targetUserId">The target user id exactly as the caller received it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> CanActOnAsync(MfaSubject actor, string targetUserId, CancellationToken cancellationToken = default);
}

/// <summary>Runs after a reset was applied, to end the target's live sessions. A throw makes the outcome <see cref="MfaAdminResetOutcome.SessionsNotRevoked"/>.</summary>
public interface ISecondFactorResetObserver
{
    /// <summary>Revokes everything the target holds that was issued on the old second factor. Must be safe to repeat.</summary>
    /// <param name="tenantId">The target's tenant.</param>
    /// <param name="targetUserId">The target user id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task OnResetAsync(string? tenantId, string targetUserId, CancellationToken cancellationToken = default);
}

/// <summary>Resets another user's second factor.</summary>
public interface ISecondFactorAdminService
{
    /// <summary>
    /// Checks, in order: the actor's fresh strong proof, the target is not the actor, the request is well formed, the tenant, then
    /// <see cref="IMfaActOnPolicy"/>. Then wipes the target's second factor in one audited transaction and revokes their sessions.
    /// </summary>
    /// <param name="actor">The acting user.</param>
    /// <param name="actorProof">The actor's proof from their own session.</param>
    /// <param name="targetUserId">The user to reset.</param>
    /// <param name="reason">A free-text reason for the audit log. Do not put personal data in it; it is never logged elsewhere.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<MfaAdminResetOutcome> ResetAsync(MfaSubject actor, MfaProof? actorProof, string targetUserId, string reason, CancellationToken cancellationToken = default);
}
