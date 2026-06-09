using System.Data;
using System.Data.Common;

namespace Themia.Framework.Data.Dapper.Connection;

internal sealed class DapperConnectionContext(IDapperConnectionFactory factory) : IDapperConnectionContext
{
    private DbConnection? connection;

    public DbTransaction? CurrentTransaction { get; private set; }

    public async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        connection ??= factory.Create();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (CurrentTransaction is not null)
            throw new InvalidOperationException(
                "A transaction is already active on this connection scope; nested transactions are not supported.");
        var conn = await GetOpenConnectionAsync(cancellationToken);
        CurrentTransaction = await conn.BeginTransactionAsync(cancellationToken);
        return CurrentTransaction;
    }

    public async ValueTask DisposeTransactionAsync()
    {
        if (CurrentTransaction is not null)
        {
            await CurrentTransaction.DisposeAsync();
            CurrentTransaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (CurrentTransaction is not null)
            await CurrentTransaction.DisposeAsync();
        if (connection is not null)
            await connection.DisposeAsync();
    }
}
