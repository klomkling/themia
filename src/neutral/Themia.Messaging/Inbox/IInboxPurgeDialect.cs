using System.Data.Common;

namespace Themia.Messaging.Inbox;

/// <summary>
/// Engine-specific deletion of expired inbox admission records. Deliberately separate from the outbox
/// purge contract: Notifications implements an outbox purge but has no inbox and must not be forced to
/// stub one.
/// </summary>
public interface IInboxPurgeDialect
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> admission records received before
    /// <paramref name="olderThan"/>. Batched; the caller loops until a batch comes back short of
    /// <paramref name="batchSize"/>.
    /// </summary>
    /// <remarks>
    /// Forgetting an admission record means a redelivery older than the window is processed as new. The
    /// window must therefore exceed the maximum age of any redelivery the sending outbox can produce.
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Records received before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; fewer than <paramref name="batchSize"/> means nothing is left.</returns>
    Task<int> PurgeAdmittedAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);
}
