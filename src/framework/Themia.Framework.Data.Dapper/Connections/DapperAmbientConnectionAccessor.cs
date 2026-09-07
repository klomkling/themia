using System.Data.Common;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.Connections;

/// <summary>Dapper implementation of <see cref="IAmbientConnectionAccessor"/> over an <see cref="IDapperConnectionContext"/>.</summary>
public sealed class DapperAmbientConnectionAccessor(IDapperConnectionContext connection) : IAmbientConnectionAccessor
{
    /// <inheritdoc/>
    /// <remarks>
    /// Checks <see cref="IDapperConnectionContext.CurrentTransaction"/> first and only opens the shared
    /// connection when a transaction is already active — calling <c>GetOpenConnectionAsync</c>
    /// unconditionally would open a connection this accessor has no use for.
    /// </remarks>
    public async Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        var transaction = connection.CurrentTransaction;
        if (transaction is null)
        {
            return null;
        }

        var dbConnection = await connection.GetOpenConnectionAsync(cancellationToken);
        return (dbConnection, transaction);
    }
}
