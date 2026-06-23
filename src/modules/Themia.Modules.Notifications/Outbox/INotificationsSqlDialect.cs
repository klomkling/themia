using System.Data.Common;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Engine-specific SQL for the outbox drainer. The drainer uses its own connection (it serves all
/// tenants), so this bypasses the tenant filter by design — the sanctioned data-layer raw-connection
/// path. Per-engine implementations live in the matching provider package
/// (<c>Themia.Modules.Notifications.PostgreSql</c>/<c>.MySql</c>/<c>.SqlServer</c>).
/// </summary>
public interface INotificationsSqlDialect
{
    /// <summary>Opens a new (closed) connection to the drain database. The caller owns its lifetime.</summary>
    /// <returns>A provider-specific <see cref="DbConnection"/> targeting the configured database.</returns>
    DbConnection CreateConnection();

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due rows — pending or failed, or a sending
    /// row whose lease has expired — and marks each as sending under <paramref name="leaseOwner"/>.
    /// Concurrent callers never claim the same row (engines use skip-locked / read-past semantics).
    /// Rows scheduled for the future (<c>scheduled_for &gt; now</c>) are not claimed.
    /// </summary>
    /// <param name="connection">An open connection from <see cref="CreateConnection"/>.</param>
    /// <param name="leaseOwner">Identifier of the claiming drainer instance, stored on each row.</param>
    /// <param name="now">The current time used to evaluate due/scheduled/lease predicates.</param>
    /// <param name="leaseExpiresAt">When the claim's lease expires and the rows become reclaimable.</param>
    /// <param name="batchSize">The maximum number of rows to claim.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The claimed rows, possibly empty.</returns>
    Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        int batchSize,
        CancellationToken ct);

    /// <summary>Marks a claimed row as sent and clears its lease.</summary>
    /// <param name="connection">An open connection from <see cref="CreateConnection"/>.</param>
    /// <param name="id">The row to complete.</param>
    /// <param name="sentAt">The delivery timestamp recorded on the row.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset sentAt, CancellationToken ct);

    /// <summary>
    /// Marks a claimed row as failed (or dead when retries are exhausted), records the new attempt
    /// count, the next retry time, and the error, and clears its lease.
    /// </summary>
    /// <param name="connection">An open connection from <see cref="CreateConnection"/>.</param>
    /// <param name="id">The row to fail.</param>
    /// <param name="attempts">The updated total attempt count.</param>
    /// <param name="nextAttemptAt">When the row becomes eligible for another claim.</param>
    /// <param name="dead">When <see langword="true"/>, the row is marked dead instead of failed.</param>
    /// <param name="error">The error message recorded on the row.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    Task FailAsync(
        DbConnection connection,
        Guid id,
        int attempts,
        DateTimeOffset nextAttemptAt,
        bool dead,
        string error,
        CancellationToken ct);
}
