using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

// After a reset the target's refresh tokens go, so no session minted on the old second factor can renew. Access tokens already issued live to expiry.
internal sealed class RevokeSessionsResetObserver(IRefreshTokenService refreshTokens) : ISecondFactorResetObserver
{
    public async Task OnResetAsync(string? tenantId, string targetUserId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(targetUserId, out var userId))
        {
            throw new ArgumentException("The target is not a user id.", nameof(targetUserId));
        }

        await refreshTokens.RevokeAllForUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }
}
