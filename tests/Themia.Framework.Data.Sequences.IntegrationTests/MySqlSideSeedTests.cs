using System.Data;
using System.Transactions;

using Dapper;

using MySqlConnector;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// MySQL seeds a new key on its own connection so a rolled-back first caller cannot leave a group of queued
/// seeders to deadlock. These tests pin what that side connection must still get right.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MySqlSequenceCollection.Name)]
public sealed class MySqlSideSeedTests(MySqlSequenceFixture fixture)
{
    private readonly string keyNamespace = Guid.NewGuid().ToString("N");

    private TransactionalSequenceAllocator Allocator() =>
        new(new SequenceOptions { ConnectionString = fixture.ConnectionString, Engine = SequenceEngine.MySql },
            new TenantContext(new TenantId("acme")));

    [Fact]
    public async Task FirstUseUnderAmbientTransactionScope_LeavesTheSeedRow_WhenTheScopeIsNotCompleted()
    {
        // A plain caller connection (AutoEnlist defaults to true): if the side seed enlisted in the ambient
        // scope, the scope disposed without Complete would take the seed row back.
        var key = $"DocNo:{keyNamespace}:Ambient";

        await using var conn = new MySqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        // The scope is entered after the caller's tx exists (a connection opened inside it could not begin a
        // MySQL transaction), so only the allocator's side connection opens under it.
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var allocated = await Allocator().NextInTransactionAsync(tx, key);
            Assert.Equal(1L, allocated);
        }

        await tx.RollbackAsync();

        await using var check = new MySqlConnection(fixture.ConnectionString);
        var seeded = await check.QuerySingleOrDefaultAsync<long?>(
            "SELECT next_value FROM themia_sequences WHERE tenant_id = 'acme' AND sequence_key = @key", new { key });
        Assert.Equal(1L, seeded);
    }

    [Fact]
    public async Task FirstUseUnderRepeatableRead_SeedsOnceAndAllocatesTwiceWithoutWaiting()
    {
        // MySQL's default isolation level: a locking read that finds no row would take a gap lock the side
        // seed then waits on. The probe is a plain read, so both calls must finish well inside the timeout.
        var key = $"DocNo:{keyNamespace}:RepeatableRead";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var allocator = Allocator();

        await using var conn = new MySqlConnection(fixture.ConnectionString);
        await conn.OpenAsync(timeout.Token);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, timeout.Token);

        var first = await allocator.NextInTransactionAsync(tx, key, timeout.Token);
        var second = await allocator.NextInTransactionAsync(tx, key, timeout.Token);
        await tx.CommitAsync(timeout.Token);

        Assert.Equal(new long[] { 1, 2 }, new[] { first, second });
    }
}
