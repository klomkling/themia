using System.Data.Common;

namespace Themia.Conversations;

/// <summary>
/// Per-database strategy for the conversation store: a connection and duplicate-key detection, plus (in the store part of this package) every SQL
/// statement the service runs against the tables created by <see cref="Migrations.ConversationSchemaMigration"/>. Implemented once per engine package.
/// </summary>
public interface IConversationDialect
{
    /// <summary>Creates an unopened connection.</summary>
    DbConnection CreateConnection();

    /// <summary>Whether <paramref name="exception"/> is a unique-constraint violation on this engine.</summary>
    /// <param name="exception">The exception a statement threw.</param>
    bool IsDuplicateKey(DbException exception);
}
