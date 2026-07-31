using System.Data.Common;

using Dapper;

using Themia.Messaging.Outbox;
using Themia.Modules.Notifications.Outbox;

namespace Themia.Modules.Notifications.MySql;

/// <summary>MySQL/MariaDB retention deletes for the notifications outbox. MySQL supports
/// <c>DELETE ... LIMIT</c> directly, so each statement is bounded without PostgreSQL's <c>ctid</c>
/// subquery: an unbounded DELETE on a large table holds long locks and bloats it, so the caller loops
/// until a batch comes back short. The table is referenced unqualified — on MySQL the
/// <c>notifications</c> schema is the database the connection string selects.</summary>
internal sealed class MySqlNotificationsPurgeDialect : IOutboxPurgeDialect<ClaimedOutboxRow>
{
    private const string PurgeSentSql = """
        DELETE FROM outbox_messages
        WHERE status = 2 AND sent_at < @olderThan
        LIMIT @batch
        """;

    private const string PurgeDeadSql = """
        DELETE FROM outbox_messages
        WHERE status = 4 AND next_attempt_at < @olderThan
        LIMIT @batch
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
