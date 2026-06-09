using System.Data.Common;

namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>Scoped holder of the one connection + ambient transaction shared by repositories and the UoW.</summary>
/// <remarks>
/// Holds exactly ONE connection per scope and is NOT safe for concurrent use within a scope:
/// the shared connection/transaction must be used sequentially (awaited one operation at a time),
/// never from overlapping/parallel async operations.
/// </remarks>
public interface IDapperConnectionContext : IAsyncDisposable
{
    /// <summary>Returns the shared, open connection (opens it on first use).</summary>
    Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>The ambient transaction, if one is open.</summary>
    DbTransaction? CurrentTransaction { get; }

    /// <summary>Begins a transaction on the shared connection.</summary>
    Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Disposes and clears the ambient transaction (after commit/rollback).</summary>
    ValueTask DisposeTransactionAsync();
}
