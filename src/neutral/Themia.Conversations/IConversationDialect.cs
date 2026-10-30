using System.Data.Common;

namespace Themia.Conversations;

/// <summary>
/// Per-database strategy for the conversation store: a connection, error classification, and the few statements whose syntax differs by engine
/// (the sequence counter and paging); the rest of the SQL is portable and lives in the service. Implemented once per engine package.
/// </summary>
public interface IConversationDialect
{
    /// <summary>Creates an unopened connection.</summary>
    DbConnection CreateConnection();

    /// <summary>Whether <paramref name="exception"/> is a unique-constraint violation on this engine.</summary>
    /// <param name="exception">The exception a statement threw.</param>
    bool IsDuplicateKey(DbException exception);

    /// <summary>Whether <paramref name="exception"/> is a deadlock or lock timeout that rolled the transaction back, which the service retries as a whole.</summary>
    /// <param name="exception">The exception a statement threw.</param>
    bool IsDeadlock(DbException exception);

    /// <summary>The paging clause that follows <c>ORDER BY</c>, taking a <c>@Limit</c> parameter (for example <c>LIMIT @Limit</c>).</summary>
    string LimitClause { get; }

    /// <summary>
    /// Adds one to <c>last_seq</c> of an <b>open</b> conversation of the tenant and returns the new value, in the caller's transaction: the row lock this takes is
    /// what orders concurrent posts. Returns <see langword="null"/> when no open conversation matched (closed, or not this tenant's).
    /// </summary>
    /// <param name="connection">The open connection of the post transaction.</param>
    /// <param name="transaction">The post transaction.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<long?> IncrementLastSeqAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken = default);
}
