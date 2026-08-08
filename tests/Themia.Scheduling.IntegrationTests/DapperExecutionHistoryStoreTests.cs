using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Quartz;
using Xunit;

namespace Themia.Scheduling.IntegrationTests;

/// <summary>
/// The ORM-free execution-history store against both real engines (coord #0071).
/// </summary>
/// <remarks>
/// Run on <b>both</b> PostgreSQL and SQL Server deliberately. The store writes raw SQL, and the column
/// it reads most is named <c>trigger</c> — a reserved keyword on SQL Server and not on PostgreSQL. An
/// unquoted identifier passes every PostgreSQL test and is a syntax error on SQL Server, which is
/// exactly the kind of defect a single-engine suite ships. The EF store never had this exposure because
/// EF quotes every identifier for you.
/// <para>
/// The schema comes from <see cref="SchedulingSchema.Migrate"/> — the real path — rather than from a
/// test-local CREATE TABLE, so these also prove the migration and the store agree on every column name.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public abstract class DapperExecutionHistoryStoreTestsBase
{
    private const string Scheduler = "test-scheduler";

    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    protected abstract DbConnection CreateConnection();

    private DapperExecutionHistoryStore NewStore() =>
        new(CreateConnection, NullLogger<DapperExecutionHistoryStore>.Instance) { SchedulerName = Scheduler };

    private static ExecutionHistoryEntry Entry(
        string fireInstanceId, string job = "group.job", string trigger = "group.trigger", int minutesAgo = 0) =>
        new()
        {
            FireInstanceId = fireInstanceId,
            SchedulerInstanceId = "instance-1",
            SchedulerName = Scheduler,
            Job = job,
            Trigger = trigger,
            ScheduledFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            ActualFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            Recovering = false,
            Vetoed = false,
        };

    [Fact]
    public async Task Save_then_Get_round_trips_every_column()
    {
        var store = NewStore();
        var entry = Entry($"fire-{Guid.NewGuid():N}");

        await store.Save(entry);
        var read = await store.Get(entry.FireInstanceId!);

        Assert.NotNull(read);
        Assert.Equal(entry.Job, read!.Job);
        Assert.Equal(entry.Trigger, read.Trigger);          // the reserved-keyword column
        Assert.Equal(entry.SchedulerInstanceId, read.SchedulerInstanceId);
        Assert.Equal(entry.SchedulerName, read.SchedulerName);
        Assert.False(read.Recovering);
        Assert.False(read.Vetoed);
    }

    [Fact]
    public async Task Save_is_an_upsert_and_the_second_write_carries_the_result()
    {
        // The second Save is the one that records how the job ended. Losing it would leave every
        // execution looking permanently in-flight on the dashboard.
        var store = NewStore();
        var entry = Entry($"fire-{Guid.NewGuid():N}");
        await store.Save(entry);

        entry.FinishedTimeUtc = DateTimeOffset.UtcNow;
        entry.ExceptionMessage = "boom";
        await store.Save(entry);

        var read = await store.Get(entry.FireInstanceId!);
        Assert.NotNull(read!.FinishedTimeUtc);
        Assert.Equal("boom", read.ExceptionMessage);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_fire_instance()
    {
        Assert.Null(await NewStore().Get($"never-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task FilterLast_returns_oldest_to_newest()
    {
        // The dashboard plots these left to right. Reversed, the chart still renders and reads backwards
        // — which is why the ordering is asserted rather than assumed to follow from the ORDER BY.
        var store = NewStore();
        var tag = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++)
        {
            await store.Save(Entry($"fire-{tag}-{i}", trigger: $"g.{tag}", minutesAgo: 10 - i));
        }

        var last = (await store.FilterLast(3)).ToList();

        Assert.Equal(3, last.Count);
        Assert.True(last[0].ActualFireTimeUtc < last[^1].ActualFireTimeUtc);
    }

    [Fact]
    public async Task FilterLastOfEveryTrigger_groups_and_limits_per_trigger()
    {
        var store = NewStore();
        var tag = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++)
        {
            await store.Save(Entry($"a-{tag}-{i}", trigger: $"g.a-{tag}", minutesAgo: 10 - i));
            await store.Save(Entry($"b-{tag}-{i}", trigger: $"g.b-{tag}", minutesAgo: 10 - i));
        }

        var grouped = (await store.FilterLastOfEveryTrigger(2)).ToList();

        Assert.Equal(2, grouped.Count(e => e.Trigger == $"g.a-{tag}"));
        Assert.Equal(2, grouped.Count(e => e.Trigger == $"g.b-{tag}"));
    }

    [Fact]
    public async Task FilterLastOfEveryJob_groups_per_job()
    {
        var store = NewStore();
        var tag = Guid.NewGuid().ToString("N");
        await store.Save(Entry($"j1-{tag}", job: $"g.one-{tag}"));
        await store.Save(Entry($"j2-{tag}", job: $"g.two-{tag}"));

        var grouped = (await store.FilterLastOfEveryJob(5)).ToList();

        Assert.Contains(grouped, e => e.Job == $"g.one-{tag}");
        Assert.Contains(grouped, e => e.Job == $"g.two-{tag}");
    }

    [Fact]
    public async Task Counters_increment_from_no_row_and_keep_counting()
    {
        // The first increment has no row to update. Dropping it there is how a counter silently
        // under-reports, which reads as jobs not running.
        var store = NewStore();
        store.SchedulerName = $"counters-{Guid.NewGuid():N}";

        Assert.Equal(0, await store.GetTotalJobsExecuted());

        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsFailed();

        Assert.Equal(2, await store.GetTotalJobsExecuted());
        Assert.Equal(1, await store.GetTotalJobsFailed());
    }

    [Fact]
    public async Task Purge_retains_the_ten_most_recent_per_trigger()
    {
        var store = NewStore();
        store.SchedulerName = $"purge-{Guid.NewGuid():N}";
        var trigger = $"g.purge-{Guid.NewGuid():N}";
        for (var i = 0; i < 15; i++)
        {
            await store.Save(new ExecutionHistoryEntry
            {
                FireInstanceId = $"purge-{Guid.NewGuid():N}",
                SchedulerName = store.SchedulerName,
                Job = "g.job",
                Trigger = trigger,
                ActualFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                Recovering = false,
                Vetoed = false,
            });
        }

        await store.Purge();

        Assert.Equal(10, (await store.FilterLast(100)).Count());
    }

    [Fact]
    public async Task Save_refuses_an_empty_fire_instance_id()
    {
        // It is the primary key: an empty one inserts a blank-key row that every later blank-key save
        // then collides with.
        var store = NewStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.Save(Entry("")));
    }

    [Fact]
    public async Task Concurrent_saves_of_one_fire_instance_all_settle()
    {
        // The plugin saves on fire and again on completion, and Quartz worker threads overlap. The
        // UPDATE-then-INSERT path must tolerate losing the race rather than surfacing it as a job failure.
        var store = NewStore();
        var id = $"race-{Guid.NewGuid():N}";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.Save(Entry(id))));

        Assert.NotNull(await store.Get(id));
    }
}

public sealed class PostgresDapperExecutionHistoryStoreTests
    : DapperExecutionHistoryStoreTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.Postgres;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        SchedulingSchema.Migrate(Engine, ConnectionString);
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

public sealed class SqlServerDapperExecutionHistoryStoreTests
    : DapperExecutionHistoryStoreTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.SqlServer;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        SchedulingSchema.Migrate(Engine, ConnectionString);
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
