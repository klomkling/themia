using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Themia.Framework.Data.EFCore.Connections;
using Themia.Framework.Data.EFCore.UnitOfWork;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Connections;

/// <summary>
/// <see cref="EfAmbientConnectionAccessor"/> must report the honest state of
/// <see cref="Microsoft.EntityFrameworkCore.Storage.DatabaseFacade.CurrentTransaction"/>: null on the common
/// path (a plain SaveChangesAsync opens and commits its own transaction), and — inside
/// ExecuteInTransactionAsync — the very connection and transaction instance EF is using, not merely a
/// non-null placeholder.
/// </summary>
public sealed class EfAmbientConnectionAccessorTests
{
    [Fact]
    public async Task Returns_null_when_no_transaction_is_open()
    {
        using var context = NewContext();
        var accessor = new EfAmbientConnectionAccessor(context);

        Assert.Null(await accessor.TryGetAsync());
    }

    [Fact]
    public async Task Returns_the_ambient_connection_and_transaction_inside_ExecuteInTransactionAsync()
    {
        using var context = NewContext();
        var accessor = new EfAmbientConnectionAccessor(context);
        var unitOfWork = new EfUnitOfWork(context);

        IDbContextTransaction? expectedTransaction = null;
        (DbConnection Connection, DbTransaction Transaction)? seen = null;
        ConnectionState? stateWhileOpen = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            expectedTransaction = context.Database.CurrentTransaction;
            seen = await accessor.TryGetAsync(ct);
            // EF owns and closes the connection once the transaction completes, so the state must be
            // captured here, inside the still-open transaction — not after ExecuteInTransactionAsync returns.
            stateWhileOpen = seen?.Connection.State;
        });

        Assert.NotNull(seen);
        Assert.Equal(ConnectionState.Open, stateWhileOpen);
        Assert.NotNull(expectedTransaction);
        Assert.Same(expectedTransaction!.GetDbTransaction(), seen.Value.Transaction);
    }

    // No DbSets are needed: the accessor and EfUnitOfWork.ExecuteInTransactionAsync only touch
    // Database.CurrentTransaction/GetDbConnection and an empty ChangeTracker, so a real SQLite connection
    // with no schema is enough — no container, no tables to create.
    private static ProbeContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new ProbeContext(options);
    }

    private sealed class ProbeContext(DbContextOptions options) : ThemiaDbContext(options);
}
