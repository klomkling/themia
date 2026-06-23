using System.Data.Common;
using Dapper;
using Npgsql;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="INotificationsSqlDialect"/> (Npgsql). Claims due
/// rows with <c>FOR UPDATE SKIP LOCKED</c> so concurrent drainers never collide.</summary>
internal sealed class PostgresNotificationsDialect : INotificationsSqlDialect
{
    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    private const string SelectDueSql = """
        SELECT id FROM notifications.outbox_messages
        WHERE next_attempt_at <= @now
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
        ORDER BY next_attempt_at
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimSql = """
        UPDATE notifications.outbox_messages
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        WHERE id = ANY(@ids)
        RETURNING id, tenant_id, channel, recipient, subject, body, attempts
        """;

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">The PostgreSQL connection string for the drain database.</param>
    public PostgresNotificationsDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);

        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            SelectDueSql, new { now, batch = batchSize }, tx, cancellationToken: ct))).ToArray();

        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct);
            return [];
        }

        var rows = await connection.QueryAsync<(Guid, string?, int, string, string?, string, int)>(
            new CommandDefinition(ClaimSql,
                new { owner = leaseOwner, exp = leaseExpiresAt, ids }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

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
