using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.MySql;

/// <summary>MySQL/MariaDB retention deletes for the messaging outbox and inbox. MySQL supports
/// <c>DELETE ... LIMIT</c> directly, so each statement is bounded without the <c>ctid</c>-subquery trick
/// PostgreSQL needs: an unbounded DELETE on a large table holds long locks and bloats it, so the caller
/// loops until a batch comes back short. Tables are referenced unqualified — on MySQL the
/// <c>messaging</c> schema is the database the connection string selects.</summary>
internal sealed class MySqlMessagingPurgeDialect
    : IOutboxPurgeDialect<ClaimedMessageRow>, IInboxPurgeDialect
{
    private const string PurgeSentSql = """
        DELETE FROM outbox_messages
        WHERE status = 2 AND sent_at < @olderThan
        LIMIT @batch
        """;

    // next_attempt_at is a deliberate proxy for time-of-death: the schema has no dedicated "died at" column.
    private const string PurgeDeadSql = """
        DELETE FROM outbox_messages
        WHERE status = 4 AND next_attempt_at < @olderThan
        LIMIT @batch
        """;

    private const string PurgeInboxSql = """
        DELETE FROM inbox_messages
        WHERE received_at < @olderThan
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

    /// <inheritdoc />
    public Task<int> PurgeAdmittedAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeInboxSql, new { olderThan, batch = batchSize }, cancellationToken: ct));
}
