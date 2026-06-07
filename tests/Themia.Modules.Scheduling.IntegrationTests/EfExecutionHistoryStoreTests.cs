using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Modules.Scheduling;
using Themia.Quartz;
using Xunit;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="EfExecutionHistoryStore"/> against a real PostgreSQL instance.
/// These tests create the schema via <c>EnsureCreatedAsync</c> for isolation; production schema
/// creation goes through the EF Core migration applied by <c>SchedulingModule.InitializeAsync</c>
/// (covered by <see cref="SchedulingModuleTests"/>).
/// </summary>
[Trait("Category", "Integration")]
public class EfExecutionHistoryStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_scheduling_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();

    private SchedulingDbContext context = null!;

    private string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        // Create the 'scheduling' schema before EnsureCreated so EF can place tables there.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS scheduling";
        await cmd.ExecuteNonQueryAsync();

        context = BuildContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await context.DisposeAsync();
        await container.DisposeAsync();
    }

    [Fact]
    public async Task Save_ThenGet_RoundTrips()
    {
        var store = BuildStore();
        var entry = MakeEntry("fire-1", "trigger-a", "job-a");

        await store.Save(entry);
        var retrieved = await store.Get("fire-1");

        Assert.NotNull(retrieved);
        Assert.Equal("fire-1", retrieved!.FireInstanceId);
        Assert.Equal("trigger-a", retrieved.Trigger);
        Assert.Equal("job-a", retrieved.Job);
        // PostgreSQL timestamptz stores microsecond precision; .NET DateTimeOffset has 100ns ticks, so a
        // round-trip truncates the sub-microsecond tick. Compare with a tolerance instead of tick-exact.
        Assert.Equal(entry.ActualFireTimeUtc, retrieved.ActualFireTimeUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal("test-scheduler", retrieved.SchedulerName);
    }

    [Fact]
    public async Task Save_IsUpsert_UpdatesExistingEntry()
    {
        var store = BuildStore();
        var entry = MakeEntry("fire-2", "trigger-b", "job-b");
        await store.Save(entry);

        entry.ExceptionMessage = "boom";
        entry.FinishedTimeUtc = DateTimeOffset.UtcNow;
        await store.Save(entry);

        var retrieved = await store.Get("fire-2");
        Assert.Equal("boom", retrieved!.ExceptionMessage);
        Assert.NotNull(retrieved.FinishedTimeUtc);
    }

    [Fact]
    public async Task FilterLast_ReturnsRecentEntries()
    {
        // Use a distinct scheduler name to avoid cross-test interference.
        var store = BuildStore("sched-filterlast");
        var t = DateTimeOffset.UtcNow;

        await store.Save(MakeEntry("fl-1", "t1", "j1", firedAt: t.AddMinutes(-3), scheduler: "sched-filterlast"));
        await store.Save(MakeEntry("fl-2", "t1", "j1", firedAt: t.AddMinutes(-2), scheduler: "sched-filterlast"));
        await store.Save(MakeEntry("fl-3", "t1", "j1", firedAt: t.AddMinutes(-1), scheduler: "sched-filterlast"));

        var results = (await store.FilterLast(2)).ToList();

        Assert.Equal(2, results.Count);
        // Most-recent two, returned oldest→newest to match InProcExecutionHistoryStore (the order the
        // dashboard histogram expects): [fl-2, fl-3].
        Assert.Equal("fl-2", results[0].FireInstanceId);
        Assert.Equal("fl-3", results[1].FireInstanceId);
        Assert.DoesNotContain(results, r => r.FireInstanceId == "fl-1");
    }

    [Fact]
    public async Task FilterLastOfEveryTrigger_GroupsCorrectly()
    {
        var store = BuildStore("sched-per-trigger");
        var t = DateTimeOffset.UtcNow;

        await store.Save(MakeEntry("pt-1", "trigger-x", "j1", firedAt: t.AddMinutes(-5), scheduler: "sched-per-trigger"));
        await store.Save(MakeEntry("pt-2", "trigger-x", "j1", firedAt: t.AddMinutes(-3), scheduler: "sched-per-trigger"));
        await store.Save(MakeEntry("pt-3", "trigger-y", "j2", firedAt: t.AddMinutes(-2), scheduler: "sched-per-trigger"));

        var results = (await store.FilterLastOfEveryTrigger(1)).ToList();

        // limit=1 per trigger → one from trigger-x and one from trigger-y
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Trigger == "trigger-x");
        Assert.Contains(results, r => r.Trigger == "trigger-y");
        // trigger-x: most recent is pt-2
        Assert.Contains(results, r => r.FireInstanceId == "pt-2");
    }

    [Fact]
    public async Task FilterLastOfEveryJob_GroupsCorrectly()
    {
        var store = BuildStore("sched-per-job");
        var t = DateTimeOffset.UtcNow;

        await store.Save(MakeEntry("pj-1", "trigger-a", "job-x", firedAt: t.AddMinutes(-5), scheduler: "sched-per-job"));
        await store.Save(MakeEntry("pj-2", "trigger-a", "job-x", firedAt: t.AddMinutes(-3), scheduler: "sched-per-job"));
        await store.Save(MakeEntry("pj-3", "trigger-b", "job-y", firedAt: t.AddMinutes(-2), scheduler: "sched-per-job"));

        var results = (await store.FilterLastOfEveryJob(1)).ToList();

        // limit=1 per job → one from job-x and one from job-y
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Job == "job-x");
        Assert.Contains(results, r => r.Job == "job-y");
        // job-x: most recent is pj-2
        Assert.Contains(results, r => r.FireInstanceId == "pj-2");
    }

    [Fact]
    public async Task Counters_IncrementAndRead()
    {
        var store = BuildStore("sched-counters");

        Assert.Equal(0, await store.GetTotalJobsExecuted());
        Assert.Equal(0, await store.GetTotalJobsFailed());

        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsFailed();

        Assert.Equal(2, await store.GetTotalJobsExecuted());
        Assert.Equal(1, await store.GetTotalJobsFailed());
    }

    [Fact]
    public async Task Get_UnknownFireInstanceId_ReturnsNull()
    {
        var store = BuildStore();
        var result = await store.Get("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public async Task Purge_RetainsTop10PerTrigger_AndDoesNotThrow()
    {
        // Regression test: Purge must translate to SQL (it builds the keep-set per trigger). It retains
        // the 10 most recent entries per trigger and deletes the rest.
        var store = BuildStore("sched-purge");
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 13; i++)
        {
            await store.Save(MakeEntry($"pg-{i}", "trigger-p", "job-p", firedAt: t.AddMinutes(i), scheduler: "sched-purge"));
        }

        await store.Purge();

        var remaining = (await store.FilterLast(100)).ToList();
        Assert.Equal(10, remaining.Count);
        // The 10 most recent survive (pg-3..pg-12); the 3 oldest (pg-0..pg-2) are purged.
        Assert.DoesNotContain(remaining, r => r.FireInstanceId == "pg-0");
        Assert.Contains(remaining, r => r.FireInstanceId == "pg-12");
    }

    [Fact]
    public async Task Save_NullOrEmptyFireInstanceId_Throws()
    {
        // Guard fires before any DB access, so no container query is needed.
        var entry = MakeEntry("", "trigger-guard", "job-guard");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => BuildStore().Save(entry));
    }

    [Fact]
    public async Task FilterLastOfEveryTrigger_WithLimit2_ReturnsOldestToNewest()
    {
        // Pins the .Reverse() on the grouping path: the 2 most-recent entries per trigger
        // must come back ordered oldest→newest (matching InProcExecutionHistoryStore's contract).
        var store = BuildStore("sched-trigger-limit2");
        var t = DateTimeOffset.UtcNow;

        var o1 = MakeEntry("o1", "trigger-rev", "job-rev", firedAt: t.AddMinutes(-3), scheduler: "sched-trigger-limit2");
        var o2 = MakeEntry("o2", "trigger-rev", "job-rev", firedAt: t.AddMinutes(-2), scheduler: "sched-trigger-limit2");
        var o3 = MakeEntry("o3", "trigger-rev", "job-rev", firedAt: t.AddMinutes(-1), scheduler: "sched-trigger-limit2");

        await store.Save(o1);
        await store.Save(o2);
        await store.Save(o3);

        // limit=2 → the 2 most-recent are o2 and o3; returned oldest→newest = [o2, o3].
        var results = (await store.FilterLastOfEveryTrigger(2)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("o2", results[0].FireInstanceId);
        Assert.Equal("o3", results[1].FireInstanceId);
    }

    [Fact]
    public async Task Save_ConcurrentCalls_NeverThrowsAndAllPersist()
    {
        // Regression test for the concurrency bug: EfExecutionHistoryStore must be safe to call
        // from multiple threads simultaneously (Quartz runs jobs on up to 10 worker threads).
        var store = BuildStore("sched-concurrent");
        const int count = 20;

        var tasks = Enumerable
            .Range(1, count)
            .Select(i => store.Save(MakeEntry($"conc-{i}", "trigger-conc", "job-conc", scheduler: "sched-concurrent")));

        // No InvalidOperationException ("A second operation was started on this context instance")
        // should be thrown here.
        await Task.WhenAll(tasks);

        // All 20 entries must be persisted.
        var results = (await store.FilterLastOfEveryTrigger(count)).ToList();
        Assert.Equal(count, results.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private DbContextOptions<SchedulingDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    private SchedulingDbContext BuildContext() =>
        new(BuildOptions());

    private IDbContextFactory<SchedulingDbContext> BuildContextFactory() =>
        new PooledDbContextFactory<SchedulingDbContext>(BuildOptions());

    private EfExecutionHistoryStore BuildStore(string schedulerName = "test-scheduler") =>
        new(BuildContextFactory(), NullLogger<EfExecutionHistoryStore>.Instance)
        {
            SchedulerName = schedulerName,
        };

    private static ExecutionHistoryEntry MakeEntry(
        string fireInstanceId,
        string trigger,
        string job,
        DateTimeOffset? firedAt = null,
        string? scheduler = null) =>
        new()
        {
            FireInstanceId = fireInstanceId,
            SchedulerName = scheduler ?? "test-scheduler",
            SchedulerInstanceId = "instance-1",
            Job = job,
            Trigger = trigger,
            ActualFireTimeUtc = firedAt ?? DateTimeOffset.UtcNow,
            ScheduledFireTimeUtc = firedAt ?? DateTimeOffset.UtcNow,
        };
}
