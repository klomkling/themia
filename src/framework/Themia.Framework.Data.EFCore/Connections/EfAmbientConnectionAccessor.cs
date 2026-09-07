using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Themia.Framework.Data.Abstractions.Connections;

namespace Themia.Framework.Data.EFCore.Connections;

/// <summary>EF Core implementation of <see cref="IAmbientConnectionAccessor"/> over a <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfAmbientConnectionAccessor(ThemiaDbContext context) : IAmbientConnectionAccessor
{
    /// <inheritdoc/>
    /// <remarks>
    /// <c>Database.CurrentTransaction</c> is null on the common path — a plain <c>SaveChangesAsync</c> opens
    /// and commits its own transaction internally, so there is nothing ambient to join. A non-null result
    /// only occurs inside <c>BeginTransactionAsync</c> or <c>ExecuteInTransactionAsync</c>.
    /// </remarks>
    public Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
        {
            return Task.FromResult<(DbConnection, DbTransaction)?>(null);
        }

        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        return Task.FromResult<(DbConnection, DbTransaction)?>((connection, dbTransaction));
    }
}
