using Dapper;
using System.Data.Common;
using Npgsql;

namespace Themia.Conversations.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IConversationDialect"/> (Npgsql).</summary>
public sealed class PostgresConversationDialect : IConversationDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public PostgresConversationDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <inheritdoc />
    public bool IsDeadlock(DbException exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    /// <inheritdoc />
    public string LimitClause => "LIMIT @Limit";

    /// <inheritdoc />
    public async Task<long?> IncrementLastSeqAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "UPDATE conversations SET last_seq = last_seq + 1 WHERE tenant_id = @TenantId AND id = @ConversationId AND state = 0 RETURNING last_seq;",
            new { TenantId = tenantId, ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
}
