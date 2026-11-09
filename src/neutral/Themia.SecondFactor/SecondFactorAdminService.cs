using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Themia.SecondFactor;

internal sealed class SecondFactorAdminService(
    IMfaResetStore resets,
    IMfaActOnPolicy actOn,
    IEnumerable<ISecondFactorResetObserver> observers,
    IOptions<SecondFactorOptions> options,
    TimeProvider time,
    ILogger<SecondFactorAdminService> logger) : ISecondFactorAdminService
{
    internal const string ResetAction = "admin.reset";
    internal const int MaxReasonLength = 500;

    public async Task<MfaAdminResetOutcome> ResetAsync(
        MfaSubject actor, MfaProof? actorProof, string targetUserId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var now = time.GetUtcNow();
        if (!ProofFreshness.IsFreshStrong(actorProof, now, options.Value.StrongAuthMaxAge))
        {
            return MfaAdminResetOutcome.StepUpRequired;
        }

        if (string.Equals(actor.UserId, targetUserId, StringComparison.Ordinal))
        {
            return MfaAdminResetOutcome.NotAllowed;
        }

        if (string.IsNullOrWhiteSpace(targetUserId) || string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
        {
            return MfaAdminResetOutcome.InvalidRequest;
        }

        if (!await MayActOnAsync(actor, targetUserId, cancellationToken).ConfigureAwait(false))
        {
            return MfaAdminResetOutcome.NotAllowed;
        }

        await resets.ResetAsync(actor.TenantId, targetUserId, actor.UserId, ResetAction, reason, now, cancellationToken).ConfigureAwait(false);
        return await RevokeSessionsAsync(actor.TenantId, targetUserId, cancellationToken).ConfigureAwait(false);
    }

    // A policy that throws is a refusal: an admin reset must never be allowed because a check could not run.
    private async Task<bool> MayActOnAsync(MfaSubject actor, string targetUserId, CancellationToken cancellationToken)
    {
        try
        {
            return await actOn.CanActOnAsync(actor, targetUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The act-on check for a second-factor admin reset failed; the reset was refused.");
            return false;
        }
    }

    private async Task<MfaAdminResetOutcome> RevokeSessionsAsync(string? tenantId, string targetUserId, CancellationToken cancellationToken)
    {
        var allRevoked = true;
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnResetAsync(tenantId, targetUserId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allRevoked = false;
                logger.LogError(ex, "A second-factor reset was applied but a session observer failed; the target's sessions may still be live. Repeat the reset.");
            }
        }

        return allRevoked ? MfaAdminResetOutcome.Succeeded : MfaAdminResetOutcome.SessionsNotRevoked;
    }
}
