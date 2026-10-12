using System.Data;
using System.Data.Common;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

public abstract class TransactionalSequenceAllocatorTestsBase
{
    private readonly string keyNamespace = Guid.NewGuid().ToString("N");

    private string Key(string name) => $"DocNo:{keyNamespace}:{name}";

    protected abstract string ConnString { get; }
    protected abstract SequenceEngine Engine { get; }

    private ITransactionalSequenceAllocator Allocator(string? tenantId = "acme") =>
        new TransactionalSequenceAllocator(
            new SequenceOptions { ConnectionString = ConnString, Engine = Engine },
            new TenantContext(tenantId is null ? null : new TenantId(tenantId)));

    private DbConnection CreateConnection()
    {
        var dialect = SequenceDialectFactory.For(Engine);
        return dialect.CreateConnection(ConnString);
    }

    [Fact]
    public async Task CallerRollback_RestoresCounter_LeavingNoGaps()
    {
        var allocator = Allocator();
        var key = Key("RollbackTest");

        // 1. Transaction 1 allocates #1 and commits
        await using (var conn1 = CreateConnection())
        {
            await conn1.OpenAsync();
            await using var tx1 = await conn1.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var val1 = await allocator.NextInTransactionAsync(tx1, key);
            Assert.Equal(1L, val1);
            await tx1.CommitAsync();
        }

        // 2. Transaction 2 allocates #2 and ROLLS BACK
        await using (var conn2 = CreateConnection())
        {
            await conn2.OpenAsync();
            await using var tx2 = await conn2.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var val2 = await allocator.NextInTransactionAsync(tx2, key);
            Assert.Equal(2L, val2);
            await tx2.RollbackAsync();
        }

        // 3. Transaction 3 allocates: MUST be #2 again because Tx 2 rolled back!
        await using (var conn3 = CreateConnection())
        {
            await conn3.OpenAsync();
            await using var tx3 = await conn3.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var val3 = await allocator.NextInTransactionAsync(tx3, key);
            Assert.Equal(2L, val3);
            await tx3.CommitAsync();
        }
    }

    [Fact]
    public async Task LazySeeding_SeedsAtOneOnFirstUse()
    {
        var allocator = Allocator();
        var key = Key("LazySeed");

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var val = await allocator.NextInTransactionAsync(tx, key);
        Assert.Equal(1L, val);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task HostAndTenant_AreCompletelyIsolated()
    {
        var key = Key("HostVsTenant");
        var tenantAllocator = Allocator("acme");
        var hostAllocator = Allocator(null);

        await using (var conn = CreateConnection())
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var tenantVal1 = await tenantAllocator.NextInTransactionAsync(tx, key);
            var hostVal1 = await hostAllocator.NextHostInTransactionAsync(tx, key);
            Assert.Equal(1L, tenantVal1);
            Assert.Equal(1L, hostVal1);
            await tx.CommitAsync();
        }

        await using (var conn = CreateConnection())
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var hostVal2 = await hostAllocator.NextHostInTransactionAsync(tx, key);
            Assert.Equal(2L, hostVal2);
            await tx.CommitAsync();
        }

        await using (var conn = CreateConnection())
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var tenantVal2 = await tenantAllocator.NextInTransactionAsync(tx, key);
            Assert.Equal(2L, tenantVal2);
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task ConcurrentAllocations_InOpenTransactions_AreSerializedAndConsecutive()
    {
        var allocator = Allocator();
        var key = Key("ConcurrentGapless");
        const int concurrentCount = 20;

        var tasks = Enumerable.Range(0, concurrentCount).Select(async _ =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var allocated = await allocator.NextInTransactionAsync(tx, key);

            // simulate minor work in tx before commit
            await Task.Delay(10);

            await tx.CommitAsync();
            return allocated;
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrentCount, results.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, concurrentCount).Select(i => (long)i).OrderBy(x => x),
            results.OrderBy(x => x));
    }

    [Fact]
    public async Task ConcurrentFirstUse_LazySeedingUnderConcurrency_IsSafeAndConsecutive()
    {
        // Tests the race condition on first use where multiple transactions simultaneously try to allocate a brand new key.
        var allocator = Allocator();
        var key = Key("ConcurrentFirstUse");
        const int concurrentCount = 10;

        var tasks = Enumerable.Range(0, concurrentCount).Select(async _ =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var allocated = await allocator.NextInTransactionAsync(tx, key);

            await Task.Delay(10);

            await tx.CommitAsync();
            return allocated;
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrentCount, results.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, concurrentCount).Select(i => (long)i).OrderBy(x => x),
            results.OrderBy(x => x));
    }

    [Fact]
    public async Task ConcurrentQueuedCallerRollback_LeavesNoGapsInCommittedValues()
    {
        // Core claim: when concurrent callers queue on the lock, and one or more roll back,
        // all successfully committed transactions must form a strictly contiguous 1..N-rolled_back run with no gaps.
        var allocator = Allocator();
        var key = Key("ConcurrentQueuedRollback");
        const int totalCallers = 10;
        // Designate caller index 3 and 7 to roll back
        var rolledBackIndices = new HashSet<int> { 3, 7 };

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, totalCallers).Select(async index =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            // Wait for all tasks to be ready to maximize concurrency pressure
            await barrier.Task;

            var allocated = await allocator.NextInTransactionAsync(tx, key);

            // Hold briefly
            await Task.Delay(15);

            if (rolledBackIndices.Contains(index))
            {
                await tx.RollbackAsync();
                return (Allocated: allocated, Committed: false);
            }

            await tx.CommitAsync();
            return (Allocated: allocated, Committed: true);
        }).ToList();

        // Launch all simultaneously
        barrier.SetResult();
        var results = await Task.WhenAll(tasks);

        var committedValues = results
            .Where(r => r.Committed)
            .Select(r => r.Allocated)
            .OrderBy(x => x)
            .ToList();

        var expectedCommittedCount = totalCallers - rolledBackIndices.Count;
        Assert.Equal(expectedCommittedCount, committedValues.Count);
        Assert.Equal(
            Enumerable.Range(1, expectedCommittedCount).Select(i => (long)i),
            committedValues);
    }
}

[Trait("Category", "Integration")]
[Collection(PostgresSequenceCollection.Name)]
public sealed class PostgresTransactionalSequenceAllocatorTests(PostgresSequenceFixture fixture)
    : TransactionalSequenceAllocatorTestsBase
{
    protected override string ConnString => fixture.ConnectionString;
    protected override SequenceEngine Engine => SequenceEngine.Postgres;
}

[Trait("Category", "Integration")]
[Collection(MySqlSequenceCollection.Name)]
public sealed class MySqlTransactionalSequenceAllocatorTests(MySqlSequenceFixture fixture)
    : TransactionalSequenceAllocatorTestsBase
{
    protected override string ConnString => fixture.ConnectionString;
    protected override SequenceEngine Engine => SequenceEngine.MySql;
}

[Trait("Category", "Integration")]
[Collection(SqlServerSequenceCollection.Name)]
public sealed class SqlServerTransactionalSequenceAllocatorTests(SqlServerSequenceFixture fixture)
    : TransactionalSequenceAllocatorTestsBase
{
    protected override string ConnString => fixture.ConnectionString;
    protected override SequenceEngine Engine => SequenceEngine.SqlServer;
}
