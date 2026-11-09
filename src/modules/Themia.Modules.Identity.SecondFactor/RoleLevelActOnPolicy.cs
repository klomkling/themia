using Themia.Modules.Identity.Abstractions;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

// The Identity rule for resetting someone's second factor: the target must exist in the actor's tenant and sit strictly below the actor
// (RoleLevelPolicy.CanActOn). The level reader answers Level5 for an unknown user, so the target is resolved first.
internal sealed class RoleLevelActOnPolicy(IUserService users, IUserLevelReader levels) : IMfaActOnPolicy
{
    public async Task<bool> CanActOnAsync(MfaSubject actor, string targetUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!TryParseCanonical(actor.UserId, out var actorId) || !TryParseCanonical(targetUserId, out var targetId))
        {
            return false;
        }

        var target = await users.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null || !string.Equals(target.TenantId?.Value, actor.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var actorLevel = await levels.GetLevelAsync(actorId, cancellationToken).ConfigureAwait(false);
        var targetLevel = await levels.GetLevelAsync(targetId, cancellationToken).ConfigureAwait(false);
        return RoleLevelPolicy.CanActOn(actorLevel, targetLevel);
    }

    // Only the canonical "D" form: a differently formatted id must not slip past the self-reset comparison in the service.
    private static bool TryParseCanonical(string value, out Guid id) =>
        Guid.TryParse(value, out id) && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
}
