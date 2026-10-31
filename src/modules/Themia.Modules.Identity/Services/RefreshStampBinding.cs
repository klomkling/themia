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
    private const int MaxSwapAttempts = 3;

    /// <summary>
    /// Rotates each user's stamp to <paramref name="newStamp"/> with a compare-and-swap from <paramref name="previousStamps"/>, then moves
    /// the user's live sessions bound to the stamp it swapped FROM to the new one. Run inside the transaction of the change that needs
    /// the rotation.
    /// </summary>
    /// <remarks>
    /// The previous stamp was read earlier and another writer (a password reset, a second admin) can commit in between. The swap is
    /// <c>WHERE security_stamp = previous</c>, so it never overwrites that write; on a miss it re-reads the stamp that is current and swaps from
    /// it, up to <see cref="MaxSwapAttempts"/> times. The rotation must still happen: a token minted after the other commit carries that
    /// stamp and the roles as they were then, and only a new stamp cuts it. Sessions are rebound from the stamp actually swapped from, never
    /// from the stale one, so a session the other writer ended stays ended while one opened after it follows. Without a repository
    /// it only rotates. When the swaps run out the stamp is rotated unconditionally (no rebind), so tokens minted since are cut and nothing stale is revived. The rebind is a set-based UPDATE of the stamp column only, so a logout or reuse revoke that commits meanwhile is never
    /// overwritten.
    /// </remarks>
    public static async Task RotateAndRebindAsync(
        IRepository<User, Guid> users,
        IRepository<RefreshToken, Guid>? refreshTokens,
        IReadOnlyDictionary<Guid, string> previousStamps,
        string newStamp,
        CancellationToken cancellationToken,
        int maxSwapAttempts = MaxSwapAttempts)
    {
        foreach (var (userId, readStamp) in previousStamps)
        {
            var previous = readStamp;
            var swapped = false;
            for (var attempt = 0; attempt < maxSwapAttempts; attempt++)
            {
                var affected = await users.UpdateWhereAsync(
                    new UserByIdAndStampAnyTenantSpec(userId, previous),
                    set => set.Set(u => u.SecurityStamp, newStamp),
                    cancellationToken).ConfigureAwait(false);
                if (affected == 1)
                {
                    if (refreshTokens is not null)
                    {
                        await refreshTokens.UpdateWhereAsync(
                            new UnrevokedRefreshTokensBoundToSpec(userId, previous),
                            set => set.Set(t => t.SecurityStamp, newStamp),
                            cancellationToken).ConfigureAwait(false);
                    }

                    swapped = true;
                    break;
                }

                var current = await users.FirstOrDefaultAsync(new UserByIdAnyTenantSpec(userId), cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    break;
                }

                previous = current.SecurityStamp;
            }

            if (!swapped)
            {
                await ForceRotateAsync(users, userId, newStamp, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The swaps ran out (or kept missing): rotate unconditionally and rebind nothing. The change that called us has already committed its
    // roles/claims, so a stamp that stays put would leave tokens minted since with the old ones valid; moving it cuts them, and the sessions
    // bound to the old stamp end at their next refresh (the safe direction). A user who no longer exists has nothing to rotate.
    private static Task ForceRotateAsync(IRepository<User, Guid> users, Guid userId, string newStamp, CancellationToken cancellationToken) =>
        users.UpdateWhereAsync(
            new UsersByIdsAnyTenantSpec([userId]),
            set => set.Set(u => u.SecurityStamp, newStamp),
            cancellationToken);
}
