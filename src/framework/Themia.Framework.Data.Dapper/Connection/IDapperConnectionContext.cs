using System.Data.Common;

namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>Scoped holder of the one connection + ambient transaction shared by repositories and the UoW.</summary>
public interface IDapperConnectionContext : IAsyncDisposable
{
    /// <summary>Returns the shared, open connection (opens it on first use).</summary>
    Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>The ambient transaction, if one is open.</summary>
    DbTransaction? CurrentTransaction { get; }

    /// <summary>Begins a transaction on the shared connection.</summary>
    Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Clears the ambient transaction reference (after commit/rollback).</summary>
    void ClearTransaction();
}
