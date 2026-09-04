using System.Transactions;

using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

using TransactionScope = System.Transactions.TransactionScope;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The defining semantic: an allocated number survives the caller's rollback.
/// </summary>
/// <remarks>
/// The obvious test — "allocate inside an outer transaction, roll it back, assert the number was not
/// reissued" — passes no matter what the implementation does, because the provider holds its own
/// connection and a rollback on a different connection cannot touch a committed row. It would stay green
/// against an implementation that had lost the semantic entirely. So this file pins the MECHANISM: first
/// it proves the check can go red, then it asserts the real behaviour.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SequenceTransactionIndependenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private ISequenceProvider Provider() =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = container.GetConnectionString(), Engine = SequenceEngine.Postgres },
            new TenantContext(new TenantId("acme")));

    // CONTROL. Allocation done the WRONG way -- on the caller's own connection and transaction -- is
    // undone by the rollback. This is what a broken implementation looks like, and it proves the
    // assertion below can actually fail.
    [Fact]
    public async Task Control_AllocatingOnTheCallersTransaction_LosesTheNumberOnRollback()
    {
        await Provider().EnsureSequenceAsync("DocNo:Control", startValue: 1);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE themia_sequences SET next_value = next_value + 1 "
                + "WHERE tenant_id = 'acme' AND sequence_key = 'DocNo:Control'", transaction: tx);
            await tx.RollbackAsync();
        }

        // Rolled back, so the counter never moved.
        Assert.Equal(1, await Provider().NextAsync("DocNo:Control"));
    }

    [Fact]
    public async Task AllocationSurvivesTheCallersRollback()
    {
        await Provider().EnsureSequenceAsync("DocNo:Survives", startValue: 1);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        long allocated;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            allocated = await Provider().NextAsync("DocNo:Survives");
            await tx.RollbackAsync();
        }

        Assert.Equal(1, allocated);

        // The number is gone for good -- a gap, which is the documented and intended outcome.
        Assert.Equal(2, await Provider().NextAsync("DocNo:Survives"));
    }

    [Fact]
    public async Task AllocationSurvivesAnAmbientSystemTransactionsScope()
    {
        // ADO providers default to Enlist=true, so a connection opened inside a TransactionScope would
        // join it and the allocation would roll back with the scope -- reissuing the number to the next
        // caller, silently. The dialects suppress enlistment; this pins it.
        await Provider().EnsureSequenceAsync("DocNo:Ambient", startValue: 1);

        long allocated;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            allocated = await Provider().NextAsync("DocNo:Ambient");
            // scope disposed without Complete() -> rollback
        }

        Assert.Equal(1, allocated);
        Assert.Equal(2, await Provider().NextAsync("DocNo:Ambient"));
    }
}
