using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

// After a reset the target's refresh tokens go, so no session minted on the old second factor can renew. Access tokens already issued live to expiry.
internal sealed class RevokeSessionsResetObserver(IRefreshTokenService refreshTokens, IUserService users) : ISecondFactorResetObserver
{
    public async Task OnResetAsync(string? tenantId, string targetUserId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(targetUserId, out var userId))
        {
            throw new ArgumentException("The target is not a user id.", nameof(targetUserId));
        }

        var revoked = await refreshTokens.RevokeAllForUserAsync(userId, cancellationToken).ConfigureAwait(false);

        // 0 is also what a user outside the current tenant scope returns, and that must not read as "sessions ended". A user that
        // resolves in scope with nothing active to revoke is fine.
        if (revoked == 0 && await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("The reset target is not visible in the current tenant scope, so none of its sessions were revoked.");
        }
    }
}
