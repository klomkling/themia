using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The whole claim of this package, on every engine it supports: no two callers ever receive the same
/// value. Locking SQL is per-engine and hand-written, so this has to run per-engine.
/// </summary>
public abstract class SequenceConcurrencyTests
{

    // Every test method gets a fresh instance from xUnit, so this namespaces THIS test's keys and
    // nothing else's. Tests that deliberately reuse one key within themselves (two tenants on the same
    // key, host versus tenant) still see a single value. Without it the suite depends on a fresh
    // container per test, which is the cost the repo's shared-fixture pattern exists to avoid.
    private readonly string keyNamespace = Guid.NewGuid().ToString("N");

    private string Key(string name) => $"DocNo:{keyNamespace}:{name}";
    protected abstract string ConnString { get; }
    protected abstract SequenceEngine Engine { get; }

    private ISequenceProvider Provider() =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = ConnString, Engine = Engine },
            new TenantContext(new TenantId("acme")));

    [Fact]
    public async Task Fifty_ConcurrentAllocations_AreAllDistinct()
    {
        await Provider().EnsureSequenceAsync(Key("Concurrent"), startValue: 1);

        var values = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Provider().NextAsync(Key("Concurrent"))));

        Assert.Equal(50, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i).OrderBy(x => x), values.OrderBy(x => x));
    }

    [Fact]
    public async Task NextRange_ReturnsContiguousValuesAndAdvancesByCount()
    {
        var sut = Provider();
        await sut.EnsureSequenceAsync(Key("Range"), startValue: 10);

        var batch = await sut.NextRangeAsync(Key("Range"), 5);

        Assert.Equal([10L, 11L, 12L, 13L, 14L], batch);
        Assert.Equal(15, await sut.NextAsync(Key("Range")));
    }

    [Fact]
    public async Task NextRange_RejectsAnAbsurdCount()
    {
        // Without an upper bound, NextRangeAsync(key, int.MaxValue) tries to allocate a 16 GB array and
        // throws OutOfMemoryException part-way through a request. An argument that can never be served
        // should be refused as an argument, up front, and named.
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Provider().NextRangeAsync(Key("Absurd"), int.MaxValue));

        Assert.Contains("count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextRange_RejectsANonPositiveCount()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Provider().NextRangeAsync(Key("Range"), 0));

    // sequence_key is varchar(100)/nvarchar(100). MySQL's old INSERT IGNORE downgraded every error class —
    // including a truncation error — to a warning, so an over-length key was silently truncated to fit
    // instead of rejected: EnsureSequenceAsync seeds the wrong (truncated) bucket, and either the full key
    // then reports "has not been seeded" or, worse, collides with an existing truncated key and absorbs
    // its seed. All three engines must reject it outright — via SequenceProvider's own length guard,
    // before the SQL ever runs — rather than truncate on MySQL alone.
    [Fact]
    public async Task EnsureSequence_RejectsAnOverLengthKeyRatherThanSilentlyTruncatingIt()
    {
        var overLengthKey = new string('x', 101);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider().EnsureSequenceAsync(overLengthKey, startValue: 1));
    }

    // Re-seeding must NOT reset a live counter on ANY engine. Task 4 proved this on Postgres only; the
    // MySQL (ON DUPLICATE KEY UPDATE) and SQL Server (INSERT ... WHERE NOT EXISTS) forms were checked as
    // SQL STRINGS and never executed. The hazard is a redeploy reissuing every number already handed out.
    [Fact]
    public async Task ReSeeding_DoesNotResetALiveCounter()
    {
        var sut = Provider();
        await sut.EnsureSequenceAsync(Key("ReSeed"), startValue: 500);
        Assert.Equal(500, await sut.NextAsync(Key("ReSeed")));

        await sut.EnsureSequenceAsync(Key("ReSeed"), startValue: 1);

        Assert.Equal(501, await sut.NextAsync(Key("ReSeed")));
    }

    [Fact]
    public async Task NextHostRange_ReturnsContiguousValues()
    {
        // The only Host method with no coverage anywhere else in the plan.
        var sut = Provider();
        await sut.EnsureHostSequenceAsync(Key("HostRange"), startValue: 40);

        Assert.Equal([40L, 41L, 42L], await sut.NextHostRangeAsync(Key("HostRange"), 3));
        Assert.Equal(43, await sut.NextHostAsync(Key("HostRange")));
    }

    [Fact]
    public async Task ACustomDialect_IsUsedInsteadOfTheEngineFactory()
    {
        // Pins the seam ISequenceDialect was made public for. Without this, changing
        // `options.Dialect ?? SequenceDialectFactory.For(options.Engine)` to ignore the override passes
        // every other test in this plan -- the override would be decoration.
        var probe = new RecordingDialect(SequenceDialectFactory.For(Engine));
        var sut = new SequenceProvider(
            new SequenceOptions { ConnectionString = ConnString, Engine = Engine, Dialect = probe },
            new TenantContext(new TenantId("acme")));

        await sut.EnsureSequenceAsync(Key("CustomDialect"), startValue: 1);
        Assert.Equal(1, await sut.NextAsync(Key("CustomDialect")));

        Assert.True(probe.WasUsed, "the provider ignored options.Dialect and fell back to the factory");
    }

    /// <summary>Delegates to a real dialect but records that it was consulted.</summary>
    private sealed class RecordingDialect(ISequenceDialect inner) : ISequenceDialect
    {
        public bool WasUsed { get; private set; }

        public System.Data.Common.DbConnection CreateConnection(string connectionString)
        {
            WasUsed = true;
            return inner.CreateConnection(connectionString);
        }

        public string SelectForUpdateSql => inner.SelectForUpdateSql;
        public string UpdateNextValueSql => inner.UpdateNextValueSql;
        public string InsertIfMissingSql => inner.InsertIfMissingSql;
    }

    [Fact]
    public async Task AnExhaustedSequence_ThrowsInsteadOfWrappingNegative()
    {
        var sut = Provider();
        await sut.EnsureSequenceAsync(Key("Exhausted"), startValue: long.MaxValue);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.NextAsync(Key("Exhausted")));
        Assert.Contains("exhausted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.Postgres;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class MySqlSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.MySql;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.MySql, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class SqlServerSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.SqlServer;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
