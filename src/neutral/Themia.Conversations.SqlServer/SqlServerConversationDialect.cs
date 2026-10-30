using Dapper;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.Conversations.SqlServer;

/// <summary>SQL Server implementation of <see cref="IConversationDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerConversationDialect : IConversationDialect
{
    private const int UniqueConstraintViolation = 2627;
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int DeadlockVictim = 1205;
    private const int LockTimeout = 1222;

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlServerConversationDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is SqlException { Number: UniqueConstraintViolation or DuplicateKeyInUniqueIndex };

    /// <inheritdoc />
    public bool IsDeadlock(DbException exception) =>
        exception is SqlException { Number: DeadlockVictim or LockTimeout };

    /// <inheritdoc />
    public string LimitClause => "OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY";

    /// <inheritdoc />
    public async Task<long?> IncrementLastSeqAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "UPDATE conversations SET last_seq = last_seq + 1 OUTPUT inserted.last_seq WHERE tenant_id = @TenantId AND id = @ConversationId AND state = 0;",
            new { TenantId = tenantId, ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
}
