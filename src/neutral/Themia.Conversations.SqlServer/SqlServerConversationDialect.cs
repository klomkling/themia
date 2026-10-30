using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.Conversations.SqlServer;

/// <summary>SQL Server implementation of <see cref="IConversationDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerConversationDialect : IConversationDialect
{
    private const int UniqueConstraintViolation = 2627;
    private const int DuplicateKeyInUniqueIndex = 2601;

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
}
