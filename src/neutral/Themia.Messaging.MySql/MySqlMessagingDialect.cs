using System.Data;
using System.Data.Common;

using Dapper;

using MySqlConnector;

using Themia.Messaging.Outbox;

namespace Themia.Messaging.MySql;

/// <summary>MySQL implementation of <see cref="IOutboxDialect{TRow}"/> for the messaging outbox
/// (MySqlConnector). MySQL has no <c>UPDATE ... RETURNING</c>, so a claim selects-and-locks due ids with
/// <c>FOR UPDATE SKIP LOCKED</c> (MySQL 8.0+), updates them, then re-reads the claimed rows
/// — all inside one transaction. Tables use the <c>messaging_</c>-prefixed name in the connection
/// string's default database rather than a dedicated schema (FluentMigrator drops <c>InSchema(...)</c> on
/// MySQL, so a schema-qualified name would mean something different per engine).
///
/// The claim transaction runs at <see cref="IsolationLevel.ReadCommitted"/>: under InnoDB's default
/// REPEATABLE READ, the range scan over the <c>(status, next_attempt_at)</c> index takes gap/next-key
/// locks that two concurrent drainers can deadlock on even with <c>SKIP LOCKED</c> (which only skips row
/// locks). READ COMMITTED takes no gap locks, so concurrent claimers lock only the rows they take. A
/// bounded retry on error 1213 covers any residual deadlock.</summary>
internal sealed class MySqlMessagingDialect : IOutboxDialect<ClaimedMessageRow>
{
    private const int MaxDeadlockRetries = 3;

    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    private const string SelectDueSql = """
        SELECT id FROM messaging_outbox_messages
        WHERE next_attempt_at <= @now
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
        ORDER BY next_attempt_at
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimSql = """
        UPDATE messaging_outbox_messages
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        WHERE id IN @ids
        """;

    private const string SelectClaimedSql = """
        SELECT id, message_id, tenant_id, type, payload, destination, origin, entity_key, version, headers, attempts
        FROM messaging_outbox_messages
        WHERE id IN @ids
        """;

    private const string CompleteSql = """
        UPDATE messaging_outbox_messages
        SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    private const string FailSql = """
        UPDATE messaging_outbox_messages
        SET status = @status, attempts = @attempts, next_attempt_at = @next,
            last_error = @error, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>. The outbox <c>id</c> and
    /// <c>message_id</c> columns are <c>CHAR(36)</c> (FluentMigrator <c>AsGuid()</c> on MySQL), so the
    /// dialect pins <c>GuidFormat=Char36</c> on its own connections regardless of the caller's setting —
    /// guaranteeing <see cref="Guid"/> values round-trip and by-id lookups match.</summary>
    /// <param name="connectionString">The MySQL connection string for the drain database.</param>
    public MySqlMessagingDialect(string connectionString)
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
    public async Task<IReadOnlyList<ClaimedMessageRow>> ClaimAsync(
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

    private static async Task<IReadOnlyList<ClaimedMessageRow>> ClaimOnceAsync(
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

        var rows = await connection.QueryAsync<ClaimedMessageRow>(new CommandDefinition(
            SelectClaimedSql, new { ids }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

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
