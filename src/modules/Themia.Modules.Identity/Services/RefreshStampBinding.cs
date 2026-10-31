using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>
/// Rebinds live refresh sessions to a user's new security stamp. A role or claim change rotates the stamp to cut stale
/// access tokens, but the session itself is still legitimate (refresh rebuilds the principal from the database), so its
/// refresh rows follow the new stamp instead of being ended by it. A caller that forgets to rebind logs the user out at the
/// next refresh, which is the safe direction.
/// </summary>
/// <remarks>
/// Only rows bound to the stamp the user held JUST BEFORE this rotation follow it. A row bound to an older stamp belongs to a
/// session that predates a credential event (a password reset that has not yet been refreshed into a revoke); rebinding it
/// would revive that session, so it stays bound to the old stamp and is refused at its next refresh.
/// </remarks>
internal static class RefreshStampBinding
{
    /// <summary>
    /// Rotates each user's stamp from <paramref name="previousStamps"/> to <paramref name="newStamp"/> with a conditional write, then
    /// moves the user's live sessions bound to the previous stamp to the new one. Run inside the transaction of the change that needs
    /// the rotation.
    /// </summary>
    /// <remarks>
    /// The previous stamp was read earlier, and a password reset can commit in between. The write is therefore
    /// <c>WHERE security_stamp = previous</c>: on a miss the user keeps the concurrent rotation's stamp (which already cut the
    /// access tokens) and is NOT rebound, so the sessions the reset ended stay ended. The rebind is a set-based UPDATE of the stamp column
    /// only, so a logout or reuse revoke that commits meanwhile is never overwritten. Without a repository it only rotates.
    /// </remarks>
    public static async Task RotateAndRebindAsync(
        IRepository<User, Guid> users,
        IRepository<RefreshToken, Guid>? refreshTokens,
        IReadOnlyDictionary<Guid, string> previousStamps,
        string newStamp,
        CancellationToken cancellationToken)
    {
        foreach (var (userId, previous) in previousStamps)
        {
            var affected = await users.UpdateWhereAsync(
                new UserByIdAndStampAnyTenantSpec(userId, previous),
                set => set.Set(u => u.SecurityStamp, newStamp),
                cancellationToken).ConfigureAwait(false);
            if (affected == 1 && refreshTokens is not null)
            {
                await refreshTokens.UpdateWhereAsync(
                    new UnrevokedRefreshTokensBoundToSpec(userId, previous),
                    set => set.Set(t => t.SecurityStamp, newStamp),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
