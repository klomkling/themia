using System.Data.Common;

namespace Themia.Framework.Data.Abstractions.Connections;

/// <summary>
/// Gives code outside the data layer access to the connection and transaction of an open unit of work,
/// without touching a raw connection itself. Implemented once per data layer (EF Core, Dapper) inside
/// that layer's own package, so a caller never needs an analyzer exemption to reach it.
/// </summary>
public interface IAmbientConnectionAccessor
{
    /// <summary>
    /// The open connection and the transaction running on it, or <c>null</c> when no transaction is open.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> result is the common case, not an edge case: on EF Core, a plain
    /// <c>SaveChangesAsync</c> call opens and commits its own transaction internally, so
    /// <c>Database.CurrentTransaction</c> is null before and after — a transaction exists only inside
    /// <c>BeginTransactionAsync</c> or <c>ExecuteInTransactionAsync</c>. Across this codebase's modules,
    /// plain <c>SaveChangesAsync</c> call sites outnumber <c>ExecuteInTransactionAsync</c> call sites by
    /// roughly ten to one. Callers must handle <c>null</c> as the normal path, not paper over it — the
    /// whole point of returning <c>null</c> instead of a connection with no transaction is to report the
    /// absence of a transaction honestly rather than let a caller believe it had enlisted when it had not.
    /// </remarks>
    Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken cancellationToken = default);
}
