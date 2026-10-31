using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
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
    private const int BatchSize = 500;

    /// <summary>
    /// Stages the rebind of each user's live rows from that user's previous stamp to <paramref name="newStamp"/> and saves it.
    /// Call inside the transaction that rotates the users' stamps. No-op without a repository.
    /// </summary>
    public static async Task RebindAsync(
        IRepository<RefreshToken, Guid>? refreshTokens,
        IUnitOfWork unitOfWork,
        IReadOnlyDictionary<Guid, string> previousStamps,
        string newStamp,
        CancellationToken cancellationToken)
    {
        if (refreshTokens is null || previousStamps.Count == 0)
        {
            return;
        }

        // Batched: Dapper sends one parameter per id and SQL Server caps a command at 2,100.
        foreach (var batch in previousStamps.Keys.Chunk(BatchSize))
        {
            var rows = await refreshTokens.ListAsync(new UnrevokedRefreshTokensByUsersSpec(batch), cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.SecurityStamp is not null && row.SecurityStamp == previousStamps[row.UserId])
                {
                    row.SecurityStamp = newStamp;
                    refreshTokens.Update(row);
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
