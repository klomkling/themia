using Dapper;
using System.Data.Common;
using MySqlConnector;

namespace Themia.Conversations.MySql;

/// <summary>MySQL 8.0.13+ implementation of <see cref="IConversationDialect"/> (MySqlConnector).</summary>
public sealed class MySqlConversationDialect : IConversationDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">MySQL 8.0.13+ connection string.</param>
    public MySqlConversationDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // GuidFormat is pinned whatever the caller passed: ConversationSchemaMigration declares its ids with .AsGuid(), which FluentMigrator renders as
        // CHAR(36) on MySQL. A shared application connection string carrying GuidFormat=Binary16 or OldGuids=true would serialise Guid parameters as 16 raw
        // bytes against a CHAR(36) column, and every lookup by id would silently stop matching. The connection is pinned to UTC for the same
        // reason ContentSchemaMigration's DATETIME(6) columns are read back as the instant that was written. UseAffectedRows is pinned to false (matched rows): the
        // store takes a row lock with a no-op UPDATE (x = x) and decides on the row count, which would read 0 under UseAffectedRows=true and skip the work silently.
        this.connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.Char36,
            DateTimeKind = MySqlDateTimeKind.Utc,
            UseAffectedRows = false,
        }.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    /// <inheritdoc />
    public bool IsDeadlock(DbException exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout };

    /// <inheritdoc />
    public string LimitClause => "LIMIT @Limit";

    /// <inheritdoc />
    public async Task<long?> IncrementLastSeqAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        // MySQL has no RETURNING: LAST_INSERT_ID(expr) stores the new value for this connection. It is read only when the UPDATE matched a row, in a
        // statement of its own, because after a zero-row UPDATE it would still hold an older value from a previous statement on the connection.
        var matched = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET last_seq = LAST_INSERT_ID(last_seq + 1) WHERE tenant_id = @TenantId AND id = @ConversationId AND state = 0;",
            new { TenantId = tenantId, ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
        return matched == 0
            ? null
            : await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT LAST_INSERT_ID();", transaction: transaction, cancellationToken: cancellationToken));
    }
}
