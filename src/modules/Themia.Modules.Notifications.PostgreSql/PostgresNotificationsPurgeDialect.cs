using System.Data.Common;

using Dapper;

using Themia.Messaging.Outbox;
using Themia.Modules.Notifications.Outbox;

namespace Themia.Modules.Notifications.PostgreSql;

/// <summary>PostgreSQL retention deletes for the notifications outbox. Bounded by <c>LIMIT</c> via a
/// <c>ctid</c> subquery so no single statement holds a long lock on a large table.</summary>
internal sealed class PostgresNotificationsPurgeDialect : IOutboxPurgeDialect<ClaimedOutboxRow>
{
    private const string PurgeSentSql = """
        DELETE FROM notifications.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM notifications.outbox_messages
            WHERE status = 2 AND sent_at < @olderThan
            LIMIT @batch
        )
        """;

    // next_attempt_at is a deliberate proxy for time-of-death: the schema has no dedicated "died at" column.
    private const string PurgeDeadSql = """
        DELETE FROM notifications.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM notifications.outbox_messages
            WHERE status = 4 AND next_attempt_at < @olderThan
            LIMIT @batch
        )
        """;

    /// <inheritdoc />
    public Task<int> PurgeSentAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeSentSql, new { olderThan, batch = batchSize }, cancellationToken: ct));

    /// <inheritdoc />
    public Task<int> PurgeDeadAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeDeadSql, new { olderThan, batch = batchSize }, cancellationToken: ct));
}
