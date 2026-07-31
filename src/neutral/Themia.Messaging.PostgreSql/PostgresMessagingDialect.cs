using System.Data.Common;

using Dapper;
using Npgsql;

using Themia.Messaging.Outbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IOutboxDialect{TRow}"/> for the messaging outbox
/// (Npgsql). Claims due rows with <c>FOR UPDATE SKIP LOCKED</c> so concurrent drainers never collide.</summary>
internal sealed class PostgresMessagingDialect(string connectionString) : IOutboxDialect<ClaimedMessageRow>
{
    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    private const string SelectDueSql = """
        SELECT id FROM messaging.outbox_messages
        WHERE next_attempt_at <= @now
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
        ORDER BY next_attempt_at
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimSql = """
        UPDATE messaging.outbox_messages
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        WHERE id = ANY(@ids)
        RETURNING id, message_id, tenant_id, type, payload, destination, origin, entity_key, version, attempts
        """;

    private const string CompleteSql = """
        UPDATE messaging.outbox_messages
        SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    private const string FailSql = """
        UPDATE messaging.outbox_messages
        SET status = @status, attempts = @attempts, next_attempt_at = @next,
            last_error = @error, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedMessageRow>> ClaimAsync(
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

        var rows = await connection.QueryAsync<ClaimedMessageRow>(new CommandDefinition(
            ClaimSql, new { owner = leaseOwner, exp = leaseExpiresAt, ids }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return rows.ToArray();
    }

    /// <inheritdoc />
    public Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset completedAt, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            CompleteSql, new { id, sentAt = completedAt }, cancellationToken: ct));

    /// <inheritdoc />
    public Task FailAsync(
        DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            FailSql,
            new { id, status = dead ? 4 : 3, attempts, next = nextAttemptAt, error },
            cancellationToken: ct));
}
