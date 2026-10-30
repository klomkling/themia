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
        // reason ContentSchemaMigration's DATETIME(6) columns are read back as the instant that was written.
        this.connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.Char36,
            DateTimeKind = MySqlDateTimeKind.Utc,
        }.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };
}
