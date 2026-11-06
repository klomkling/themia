using System.Data;
using System.Data.Common;
using Themia.Framework.Data.Abstractions.Filtering;
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

    [Fact]
    public async Task CommitAsync_WhenCommitThrows_PutsRecordedVersionsBack()
    {
        var entity = new VersionedStub { ConcurrencyVersion = 4 };   // a save already moved it from 3 to 4
        var ctx = new FakeConnectionContext(new FakeTx { CommitThrows = true });
        var uow = NewUnitOfWork(ctx);
        var scope = await uow.BeginTransactionAsync();
        uow.RememberVersion(entity, 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync());

        Assert.Equal(3, entity.ConcurrencyVersion);   // nobody can say whether it landed: behind the row is the safe side
        await scope.DisposeAsync();                    // the failed commit disposed the tx, so Dispose cannot restore: it was done above
    }

    [Fact]
    public async Task CommitAsync_WhenItSucceeds_LeavesTheNewVersionAndForgetsTheRecord()
    {
        var entity = new VersionedStub { ConcurrencyVersion = 4 };
        var uow = NewUnitOfWork(new FakeConnectionContext(new FakeTx()));
        var scope = await uow.BeginTransactionAsync();
        uow.RememberVersion(entity, 3);

        await scope.CommitAsync();
        await scope.DisposeAsync();

        Assert.Equal(4, entity.ConcurrencyVersion);
    }

    [Fact]
    public async Task RollbackAsync_PutsRecordedVersionsBack_AndDisposeAfterwardsDoesNotRestoreAgain()
    {
        var entity = new VersionedStub { ConcurrencyVersion = 4 };
        var uow = NewUnitOfWork(new FakeConnectionContext(new FakeTx()));
        var scope = await uow.BeginTransactionAsync();
        uow.RememberVersion(entity, 3);

        await scope.RollbackAsync();
        entity.ConcurrencyVersion = 9;                 // a later committed write moved it on
        await scope.DisposeAsync();

        Assert.Equal(9, entity.ConcurrencyVersion);
    }

    [Fact]
    public async Task DisposeAsync_WithoutCommit_PutsRecordedVersionsBack()
    {
        var entity = new VersionedStub { ConcurrencyVersion = 4 };
        var uow = NewUnitOfWork(new FakeConnectionContext(new FakeTx()));
        var scope = await uow.BeginTransactionAsync();
        uow.RememberVersion(entity, 3);

        await scope.DisposeAsync();

        Assert.Equal(3, entity.ConcurrencyVersion);
    }

    [Fact]
    public async Task RememberVersion_FirstValueWins()
    {
        var entity = new VersionedStub { ConcurrencyVersion = 5 };   // saved twice: 3 -> 4 -> 5
        var uow = NewUnitOfWork(new FakeConnectionContext(new FakeTx()));
        var scope = await uow.BeginTransactionAsync();
        uow.RememberVersion(entity, 3);
        uow.RememberVersion(entity, 4);

        await scope.RollbackAsync();

        Assert.Equal(3, entity.ConcurrencyVersion);
    }

    private sealed class VersionedStub : Themia.Framework.Core.Abstractions.Entities.IVersionedEntity
    {
        public long ConcurrencyVersion { get; set; }
    }

    // Only the connection context is exercised on the BeginTransaction -> Commit/Dispose path; the remaining
    // dependencies are null! because they are never touched there. filterScope gets a real instance so the
    // helper stays safe to reuse on a path that reaches TenantScoped.
    private static DapperUnitOfWork NewUnitOfWork(IDapperConnectionContext ctx) =>
        new(ctx, registry: null!, compiler: null!, tenantContext: null!, currentUser: null!,
            filterScope: new DataFilterScope(), timeProvider: TimeProvider.System,
            sqlExceptionInterpreter: null!);

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
