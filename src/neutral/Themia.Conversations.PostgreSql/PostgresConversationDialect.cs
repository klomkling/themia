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
}
