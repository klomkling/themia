using System.Data.Common;

namespace Themia.Messaging.Outbox;

/// <summary>
/// Engine-specific deletion of terminal outbox rows. Separate from <see cref="IOutboxDialect{TRow}"/> so an
/// outbox can be drained without granting it delete authority, and generic over the row type so several
/// outboxes can be purged independently in one container.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape identifying which outbox this purges.</typeparam>
public interface IOutboxPurgeDialect<TRow>
    where TRow : IClaimedRow
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> successfully-sent rows older than
    /// <paramref name="olderThan"/>. Batched deliberately: an unbounded DELETE on a large outbox holds
    /// long locks and bloats the table, so the caller loops until a batch comes back short of
    /// <paramref name="batchSize"/>.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Rows sent before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; fewer than <paramref name="batchSize"/> means nothing is left.</returns>
    Task<int> PurgeSentAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> dead-lettered rows older than <paramref name="olderThan"/>.
    /// Kept on a longer window than sent rows: each dead row is an unresolved delivery failure.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Rows that died before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; fewer than <paramref name="batchSize"/> means nothing is left.</returns>
    Task<int> PurgeDeadAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);
}
