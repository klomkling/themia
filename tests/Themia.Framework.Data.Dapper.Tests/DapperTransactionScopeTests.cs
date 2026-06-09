using System.Data;
using System.Data.Common;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.UnitOfWork;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

/// <summary>
/// Covers the transaction-scope error paths that the integration suite cannot easily force: a commit that
/// throws must surface its own exception (never a masking rollback) and must still dispose the transaction,
/// while a scope disposed without an explicit commit/rollback must roll back exactly once as a safety net.
/// </summary>
public sealed class DapperTransactionScopeTests
{
    [Fact]
    public async Task CommitAsync_WhenCommitThrows_SurfacesCommitError_AndDoesNotRollBack()
    {
        var tx = new FakeTx { CommitThrows = true, RollbackThrows = true };
        var ctx = new FakeConnectionContext(tx);
        var scope = await NewUnitOfWork(ctx).BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync());

        Assert.Equal("commit-boom", ex.Message);          // the original commit error, not "rollback-boom"
        Assert.False(tx.RollbackAttempted);               // the failed commit already disposed the tx
        Assert.Equal(1, ctx.DisposeTransactionCalls);

        await scope.DisposeAsync();                        // no live transaction left -> no second rollback
        Assert.Equal(1, ctx.DisposeTransactionCalls);
    }

    [Fact]
    public async Task DisposeAsync_WithoutCommit_RollsBackOnce()
    {
        var tx = new FakeTx();
        var ctx = new FakeConnectionContext(tx);
        var scope = await NewUnitOfWork(ctx).BeginTransactionAsync();

        await scope.DisposeAsync();

        Assert.True(tx.RollbackAttempted);
        Assert.Equal(1, ctx.DisposeTransactionCalls);
    }

    // Only the connection context is exercised on the BeginTransaction -> Commit/Dispose path.
    private static DapperUnitOfWork NewUnitOfWork(IDapperConnectionContext ctx) =>
        new(ctx, registry: null!, compiler: null!, tenantContext: null!, currentUser: null!, timeProvider: TimeProvider.System);

    private sealed class FakeConnectionContext(DbTransaction tx) : IDapperConnectionContext
    {
        public DbTransaction? CurrentTransaction { get; private set; }
        public int DisposeTransactionCalls { get; private set; }

        public Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            CurrentTransaction = tx;
            return Task.FromResult(tx);
        }

        public ValueTask DisposeTransactionAsync()
        {
            DisposeTransactionCalls++;
            CurrentTransaction = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTx : DbTransaction
    {
        public bool CommitThrows { get; init; }
        public bool RollbackThrows { get; init; }
        public bool RollbackAttempted { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => RollbackAttempted = true;

        public override Task CommitAsync(CancellationToken cancellationToken = default) =>
            CommitThrows ? throw new InvalidOperationException("commit-boom") : Task.CompletedTask;

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackAttempted = true;
            return RollbackThrows
                ? throw new InvalidOperationException("rollback-boom")
                : Task.CompletedTask;
        }
    }
}
