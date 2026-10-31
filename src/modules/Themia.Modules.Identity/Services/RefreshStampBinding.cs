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
internal static class RefreshStampBinding
{
    private const int BatchSize = 500;

    /// <summary>Binds every unrevoked refresh row of <paramref name="userIds"/> to <paramref name="stamp"/>. No-op without a repository.</summary>
    public static async Task RebindAsync(
        IRepository<RefreshToken, Guid>? refreshTokens, IReadOnlyCollection<Guid> userIds, string stamp, CancellationToken cancellationToken)
    {
        if (refreshTokens is null)
        {
            return;
        }

        // Batched: Dapper sends one parameter per id and SQL Server caps a command at 2,100.
        foreach (var batch in userIds.Chunk(BatchSize))
        {
            await refreshTokens.UpdateWhereAsync(
                new UnrevokedRefreshTokensByUsersSpec(batch),
                set => set.Set(t => t.SecurityStamp, stamp),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
