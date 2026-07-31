using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.SqlServer;

/// <summary>SQL Server retention deletes for the messaging outbox and inbox. SQL Server has no
/// <c>DELETE ... LIMIT</c>, so each statement uses <c>DELETE TOP (@batch)</c> instead: an unbounded
/// DELETE on a large table holds long locks and bloats it, so the caller loops until a batch comes back
/// short.</summary>
internal sealed class SqlServerMessagingPurgeDialect
    : IOutboxPurgeDialect<ClaimedMessageRow>, IInboxPurgeDialect
{
    private const string PurgeSentSql = """
        DELETE TOP (@batch) FROM [messaging].[outbox_messages]
        WHERE status = 2 AND sent_at < @olderThan
        """;

    private const string PurgeDeadSql = """
        DELETE TOP (@batch) FROM [messaging].[outbox_messages]
        WHERE status = 4 AND next_attempt_at < @olderThan
        """;

    private const string PurgeInboxSql = """
        DELETE TOP (@batch) FROM [messaging].[inbox_messages]
        WHERE received_at < @olderThan
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
