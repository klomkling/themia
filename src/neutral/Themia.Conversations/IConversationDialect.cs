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

    /// <summary>
    /// The table hint that follows the <c>conversation_notify_cursors n</c> alias in the notification claim select: <c>WITH (UPDLOCK, READPAST, ROWLOCK)</c> on SQL Server, empty where
    /// <see cref="CursorClaimRowLockClause"/> does the work. The default throws, so a custom dialect that never implemented it fails loudly instead of claiming without a lock.
    /// </summary>
    string CursorClaimTableHint => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(CursorClaimTableHint)}, which the notification drain needs.");

    /// <summary>
    /// The row-lock clause that ends the notification claim select, locking only the cursor alias <c>n</c> and skipping rows another drain holds
    /// (<c>FOR UPDATE OF n SKIP LOCKED</c> on PostgreSQL and MySQL 8.0.1+), empty on SQL Server. The default throws.
    /// </summary>
    string CursorClaimRowLockClause => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(CursorClaimRowLockClause)}, which the notification drain needs.");
}
