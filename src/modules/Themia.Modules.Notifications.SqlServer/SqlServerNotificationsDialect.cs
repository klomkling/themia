using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.SqlServer;

/// <summary>SQL Server implementation of <see cref="INotificationsSqlDialect"/>
/// (Microsoft.Data.SqlClient). The claim is a single atomic statement: an ordered CTE feeds
/// <c>UPDATE ... WITH (READPAST, UPDLOCK, ROWLOCK) ... OUTPUT inserted.*</c>, so concurrent drainers skip
/// each other's locked rows, claim the oldest-due rows first (FIFO, like the PostgreSQL/MySQL dialects),
/// and no explicit transaction is needed.</summary>
internal sealed class SqlServerNotificationsDialect : INotificationsSqlDialect
{
    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    // The CTE orders by next_attempt_at so the claim is FIFO; TOP(@batch) + READPAST inside the CTE keep
    // the claim atomic and skip rows another drainer already locked.
    private const string ClaimSql = """
        WITH due AS (
            SELECT TOP (@batch) id, tenant_id, channel, recipient, subject, body, attempts,
                   status, lease_owner, lease_expires_at
            FROM notifications.outbox_messages WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE next_attempt_at <= @now
              AND (scheduled_for IS NULL OR scheduled_for <= @now)
              AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
            ORDER BY next_attempt_at
        )
        UPDATE due
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        OUTPUT inserted.id, inserted.tenant_id, inserted.channel, inserted.recipient,
               inserted.subject, inserted.body, inserted.attempts
        """;

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">The SQL Server connection string for the drain database.</param>
    public SqlServerNotificationsDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(Guid, string?, int, string, string?, string, int)>(
            new CommandDefinition(ClaimSql,
                new { batch = batchSize, owner = leaseOwner, exp = leaseExpiresAt, now },
                cancellationToken: ct));

        return rows
            .Select(r => new ClaimedOutboxRow(r.Item1, r.Item2, (NotificationChannel)r.Item3, r.Item4, r.Item5, r.Item6, r.Item7))
            .ToList();
    }

    /// <inheritdoc />
    public Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset sentAt, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notifications.outbox_messages SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL WHERE id = @id",
            new { id, sentAt }, cancellationToken: ct));

    /// <inheritdoc />
    public Task FailAsync(DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE notifications.outbox_messages
            SET status = @status, attempts = @attempts, next_attempt_at = @next, last_error = @error,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id
            """, new { id, status = dead ? 4 : 3, attempts, next = nextAttemptAt, error }, cancellationToken: ct));
}
