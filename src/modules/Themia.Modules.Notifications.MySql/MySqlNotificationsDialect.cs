using System.Data;
using System.Data.Common;
using Dapper;
using MySqlConnector;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.MySql;

/// <summary>MySQL/MariaDB implementation of <see cref="INotificationsSqlDialect"/> (MySqlConnector).
/// MySQL has no <c>UPDATE ... RETURNING</c>, so a claim selects-and-locks due ids with
/// <c>FOR UPDATE SKIP LOCKED</c> (MySQL 8.0+/MariaDB 10.6+), updates them, then re-reads the claimed
/// rows — all inside one transaction. On MySQL the <c>notifications</c> schema is the database the
/// connection string selects, so tables are referenced unqualified.
///
/// The claim transaction runs at <see cref="IsolationLevel.ReadCommitted"/>: under InnoDB's default
/// REPEATABLE READ, the range scan over the <c>(status, next_attempt_at)</c> index takes gap/next-key
/// locks that two concurrent drainers can deadlock on even with <c>SKIP LOCKED</c> (which only skips
/// row locks). READ COMMITTED takes no gap locks, so concurrent claimers lock only the rows they take.
/// A bounded retry on error 1213 covers any residual deadlock.</summary>
internal sealed class MySqlNotificationsDialect : INotificationsSqlDialect
{
    private const int MaxDeadlockRetries = 3;

    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    private const string SelectDueSql = """
        SELECT id FROM outbox_messages
        WHERE next_attempt_at <= @now
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
        ORDER BY next_attempt_at
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimSql = """
        UPDATE outbox_messages
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        WHERE id IN @ids
        """;

    private const string SelectClaimedSql = """
        SELECT id, tenant_id, channel, recipient, subject, body, attempts
        FROM outbox_messages
        WHERE id IN @ids
        """;

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>. The outbox <c>id</c> column is
    /// <c>CHAR(36)</c> (FluentMigrator <c>AsGuid()</c> on MySQL), so the dialect pins
    /// <c>GuidFormat=Char36</c> on its own connections regardless of the caller's setting — guaranteeing
    /// <see cref="System.Guid"/> values round-trip and by-id lookups match.</summary>
    /// <param name="connectionString">The MySQL/MariaDB connection string for the drain database.</param>
    public MySqlNotificationsDialect(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            OldGuids = false, // clear the legacy flag first (OldGuids + GuidFormat are mutually exclusive)
            GuidFormat = MySqlGuidFormat.Char36,
        };
        this.connectionString = builder.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ClaimOnceAsync(connection, leaseOwner, now, leaseExpiresAt, batchSize, ct)
                    .ConfigureAwait(false);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < MaxDeadlockRetries)
            {
                // Transient InnoDB deadlock — the transaction is already rolled back; retry the claim.
            }
        }
    }

    private static async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimOnceAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        // READ COMMITTED so the SKIP LOCKED range scan takes no gap locks (see class remarks).
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            SelectDueSql, new { now, batch = batchSize }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToArray();

        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return [];
        }

        await connection.ExecuteAsync(new CommandDefinition(
            ClaimSql, new { owner = leaseOwner, exp = leaseExpiresAt, ids }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<(Guid, string?, int, string, string?, string, int)>(
            new CommandDefinition(SelectClaimedSql, new { ids }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new ClaimedOutboxRow(r.Item1, r.Item2, (NotificationChannel)r.Item3, r.Item4, r.Item5, r.Item6, r.Item7))
            .ToList();
    }

    /// <inheritdoc />
    public Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset sentAt, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE outbox_messages SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL WHERE id = @id",
            new { id, sentAt }, cancellationToken: ct));

    /// <inheritdoc />
    public Task FailAsync(DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE outbox_messages
            SET status = @status, attempts = @attempts, next_attempt_at = @next, last_error = @error,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id
            """, new { id, status = dead ? 4 : 3, attempts, next = nextAttemptAt, error }, cancellationToken: ct));
}
