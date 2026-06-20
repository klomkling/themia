using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Logging;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Scheduling;
using Themia.Quartz;
using Xunit;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Lifecycle integration tests for <see cref="SchedulingModule"/> against a real database:
/// <see cref="SchedulingModule.InitializeAsync"/> applies the FluentMigrator schema migration, and the
/// registered <see cref="IExecutionHistoryStore"/> resolves to the EF-backed store and round-trips data.
/// Run once per engine via the derived classes.
/// </summary>
public abstract class SchedulingModuleTestsBase
{
    protected abstract string ConnectionString { get; }
    protected abstract string ProviderName { get; }

    [Fact]
    public async Task InitializeAsync_RunsFmMigration_AndStoreRoundTrips()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        // The table exists (FM migration applied) — CountAsync would throw if it were absent.
        var existingCount = await context.ExecutionHistory.CountAsync();
        Assert.Equal(0, existingCount);

        var store = scope.ServiceProvider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var entry = new ExecutionHistoryEntry
        {
            FireInstanceId = "module-fire-1",
            SchedulerName = "module-test",
            SchedulerInstanceId = "instance-1",
            Job = "job-a",
            Trigger = "trigger-a",
            ActualFireTimeUtc = DateTimeOffset.UtcNow,
            ScheduledFireTimeUtc = DateTimeOffset.UtcNow,
        };

        await store.Save(entry);
        var retrieved = await store.Get("module-fire-1");

        Assert.NotNull(retrieved);
        Assert.Equal("job-a", retrieved!.Job);
    }

    [Fact]
    public async Task InitializeAsync_AdoptsExistingSchema_AndRestoresMissingIndex_OnCutover()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        // First run creates the full schema: both tables, the index, and the FluentMigrator VersionInfo row.
        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        // Simulate the EF→FM cutover against a partially-degraded database: the tables remain, the index has
        // been lost, and FluentMigrator has no record of the migration (a pre-0.4.7 deployment had no
        // VersionInfo). Without per-object guards the re-run would either fail on CREATE TABLE (already exists)
        // or silently skip the missing index behind the table guard.
        await context.Database.ExecuteSqlRawAsync(DropHistoryIndexSql);
        await context.Database.ExecuteSqlRawAsync(ClearVersionInfoSql);
        Assert.False(await HistoryIndexExistsAsync(context));

        // Re-run must adopt the existing tables (no "already exists" failure) AND recreate the missing index.
        await module.InitializeAsync(provider);

        Assert.True(await HistoryIndexExistsAsync(context));

        var store = scope.ServiceProvider.GetRequiredService<IExecutionHistoryStore>();
        await store.Save(new ExecutionHistoryEntry
        {
            FireInstanceId = "cutover-1",
            SchedulerName = "module-test",
            ActualFireTimeUtc = DateTimeOffset.UtcNow,
        });
        Assert.NotNull(await store.Get("cutover-1"));
    }

    [Fact]
    public async Task InitializeAsync_CreatesQuartzAdoJobStoreSchema()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        // qrtz_job_details exists in the quartz schema → COUNT succeeds (throws if absent).
        var count = await context.Database.SqlQueryRaw<int>(QrtzJobDetailsCountSql).ToListAsync();
        Assert.Equal(0, count[0]);
    }

    [Fact]
    public async Task InitializeAsync_AdoptsExistingQuartzSchema_OnCutoverReplay()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        // First run creates the quartz schema, the qrtz_* tables, and the FluentMigrator VersionInfo row.
        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        // Simulate the cutover replay: the qrtz_* objects already exist but FluentMigrator has no record of
        // the migration (a deployment with no VersionInfo), forcing a replay. Without the schema/table
        // existence guards in QuartzAdoJobStoreMigration the re-run fails on a duplicate CREATE SCHEMA quartz.
        await context.Database.ExecuteSqlRawAsync(ClearVersionInfoSql);

        // Re-run must adopt the existing quartz schema without throwing, leaving qrtz_job_details in place.
        await module.InitializeAsync(provider);

        var count = await context.Database.SqlQueryRaw<int>(QrtzJobDetailsCountSql).ToListAsync();
        Assert.Equal(0, count[0]);
    }

    [Fact]
    public async Task PersistentScheduler_SurvivesRestart_ViaAdoJobStore()
    {
        // First "process": migrate, start a scheduler, schedule a durable job, shut down.
        var p1 = BuildModuleServices();
        await new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" }).InitializeAsync(p1);

        // Quartz's logging is a process-global static (LogContext). Two real processes each own it; in this
        // single-process test the two providers share it, so point it at the live provider before resolving
        // the scheduler — otherwise p2 would log through p1's disposed ILoggerFactory after p1.DisposeAsync().
        LogContext.SetCurrentLogProvider(p1.GetRequiredService<ILoggerFactory>());

        var factory1 = p1.GetRequiredService<ISchedulerFactory>();
        var scheduler1 = await factory1.GetScheduler();
        await scheduler1.Start();

        var jobKey = new JobKey("persisted-job", "persisted-group");
        var job = JobBuilder.Create<NoOpJob>().WithIdentity(jobKey).StoreDurably().Build();
        var trigger = TriggerBuilder.Create()
            .WithIdentity("persisted-trigger", "persisted-group")
            .ForJob(jobKey)
            .StartAt(DateTimeOffset.UtcNow.AddDays(1))
            .Build();
        await scheduler1.ScheduleJob(job, trigger);
        await scheduler1.Shutdown(waitForJobsToComplete: false);
        await p1.DisposeAsync();

        // Second "process": a fresh service provider over the SAME container DB; the job must still be there.
        var p2 = BuildModuleServices();
        LogContext.SetCurrentLogProvider(p2.GetRequiredService<ILoggerFactory>());
        var factory2 = p2.GetRequiredService<ISchedulerFactory>();
        var scheduler2 = await factory2.GetScheduler();
        await scheduler2.Start();

        Assert.True(await scheduler2.CheckExists(jobKey));
        var loaded = await scheduler2.GetTrigger(new TriggerKey("persisted-trigger", "persisted-group"));
        Assert.NotNull(loaded);

        await scheduler2.Shutdown(waitForJobsToComplete: false);
        await p2.DisposeAsync();
    }

    [Fact]
    public async Task PersistentScheduler_RecordsExecutionHistory_ToEfStore()
    {
        var provider = BuildModuleServices();
        await new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" }).InitializeAsync(provider);

        // Mirror the restart test: Quartz's LogContext is a process-global static; point it at this provider's
        // live ILoggerFactory so the scheduler doesn't log through a disposed factory (ObjectDisposedException).
        LogContext.SetCurrentLogProvider(provider.GetRequiredService<ILoggerFactory>());

        var factory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await factory.GetScheduler();
        await scheduler.Start();

        var jobKey = new JobKey("history-job", "history-group");
        var job = JobBuilder.Create<NoOpJob>().WithIdentity(jobKey).Build();
        var trigger = TriggerBuilder.Create().WithIdentity("history-trigger", "history-group").ForJob(jobKey).StartNow().Build();
        await scheduler.ScheduleJob(job, trigger);

        // Wait for the job to fire and the history listener to write to the EF store (the SAME singleton the
        // plugin must use). Without FIX 1 the plugin caches the in-proc store and this stays 0.
        var store = provider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);
        var recorded = false;
        for (var i = 0; i < 50 && !recorded; i++)
        {
            await Task.Delay(100);
            if (await store.GetTotalJobsExecuted() > 0) recorded = true;
        }

        await scheduler.Shutdown(waitForJobsToComplete: true);
        await provider.DisposeAsync();

        Assert.True(recorded, "The job execution was not recorded in the EF execution-history store — the plugin likely bound to the in-proc fallback store.");
    }

    [Fact]
    public void ConfigureServices_RegistersStoreAndDashboardOptions()
    {
        var provider = BuildModuleServices();

        var store = provider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var quartzOptions = provider.GetRequiredService<ThemiaQuartzOptions>();
        Assert.Equal("/jobs", quartzOptions.VirtualPathRoot);
        Assert.NotNull(quartzOptions.Authorize);
    }

    [Fact]
    public async Task DefaultAuthorize_AllowsAuthenticated_DeniesAnonymous()
    {
        var provider = BuildModuleServices();
        var authorize = provider.GetRequiredService<ThemiaQuartzOptions>().Authorize;
        Assert.NotNull(authorize);

        var authenticatedCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
        };
        Assert.True(await authorize!(authenticatedCtx));

        var anonymousCtx = new DefaultHttpContext();
        Assert.False(await authorize(anonymousCtx));
    }

    private ServiceProvider BuildModuleServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(ProviderName));

        new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" })
            .ConfigureServices(services);

        return services.BuildServiceProvider();
    }

    // Engine-specific maintenance SQL for the cutover test. Declared abstract so each engine supplies all of
    // them — the compiler enforces completeness when a new engine is added, instead of a ternary silently
    // falling through to the wrong dialect. Each must be schema-qualified and scoped to the scheduling objects.
    protected abstract string DropHistoryIndexSql { get; }
    protected abstract string ClearVersionInfoSql { get; }
    protected abstract string HistoryIndexCountSql { get; }

    // qrtz_job_details COUNT(*) per engine — table exists (in the quartz schema) iff this query succeeds.
    protected abstract string QrtzJobDetailsCountSql { get; }

    private async Task<bool> HistoryIndexExistsAsync(SchedulingDbContext context)
    {
        // SqlQueryRaw<int> maps a single-column result whose column is aliased "Value".
        var counts = await context.Database.SqlQueryRaw<int>(HistoryIndexCountSql).ToListAsync();
        return counts[0] > 0;
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_scheduling_module_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.Postgres;

    protected override string DropHistoryIndexSql =>
        "DROP INDEX scheduling.ix_execution_history_scheduler_trigger_fired";
    protected override string ClearVersionInfoSql =>
        "DELETE FROM public.\"VersionInfo\"";
    protected override string HistoryIndexCountSql =>
        "SELECT COUNT(*) AS \"Value\" FROM pg_indexes WHERE schemaname = 'scheduling' AND indexname = 'ix_execution_history_scheduler_trigger_fired'";
    protected override string QrtzJobDetailsCountSql =>
        "SELECT COUNT(*) AS \"Value\" FROM quartz.qrtz_job_details";

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class SqlServerSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.SqlServer;

    protected override string DropHistoryIndexSql =>
        "DROP INDEX ix_execution_history_scheduler_trigger_fired ON scheduling.execution_history";
    protected override string ClearVersionInfoSql =>
        "DELETE FROM [dbo].[VersionInfo]";
    // sys.indexes names are unique per-table, not per-database — scope by object_id so an identically-named
    // index on another table cannot produce a false positive.
    protected override string HistoryIndexCountSql =>
        "SELECT COUNT(*) AS Value FROM sys.indexes WHERE name = 'ix_execution_history_scheduler_trigger_fired' " +
        "AND object_id = OBJECT_ID('scheduling.execution_history')";
    protected override string QrtzJobDetailsCountSql =>
        "SELECT COUNT(*) AS Value FROM quartz.qrtz_job_details";

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

/// <summary>
/// Verifies the persistent Quartz store works on a CASE-SENSITIVE SQL Server collation. The verbatim Quartz
/// DDL creates uppercase <c>QRTZ_*</c> tables; under a case-insensitive collation (the default the main suite
/// uses) a lowercase <c>TablePrefix</c> or existence guard still resolves them, masking the mismatch. Under
/// this CS collation it does not — so this pins both the migration's existence guard (cutover replay) and the
/// runtime <c>TablePrefix</c> to the uppercase names. A single self-disposing test, to avoid the shared-container
/// lock contention that running the whole suite under one CS container would introduce.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerCaseSensitiveCollationTests : IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithEnvironment("MSSQL_COLLATION", "SQL_Latin1_General_CP1_CS_AS")
            .Build();

    [Fact]
    public async Task PersistentStore_WorksOnCaseSensitiveCollation()
    {
        var jobKey = new JobKey("cs-job", "cs-group");

        // Process 1: create the schema (uppercase QRTZ_* tables) and prove Quartz's runtime TablePrefix
        // resolves them — pre-fix the lowercase 'quartz.qrtz_' prefix yields "Invalid object name" under CS.
        var p1 = BuildServices();
        try
        {
            await new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "cs-test" }).InitializeAsync(p1);
            LogContext.SetCurrentLogProvider(p1.GetRequiredService<ILoggerFactory>());
            // Build the scheduler (this runs AdoJobStore ValidateSchema against the uppercase qrtz_* tables —
            // the actual CS-collation prefix check) but do NOT Start() it: Start spawns the AdoJobStore
            // check-in thread, whose connection isn't released synchronously on Shutdown and intermittently
            // deadlocks process 2's FluentMigrator DDL on the shared container. Scheduling + reading the
            // durable job below exercises the TablePrefix on writes/reads without a running scheduler.
            var scheduler1 = await p1.GetRequiredService<ISchedulerFactory>().GetScheduler();

            var job = JobBuilder.Create<NoOpJob>().WithIdentity(jobKey).StoreDurably().Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity("cs-trigger", "cs-group").ForJob(jobKey)
                .StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build();
            await scheduler1.ScheduleJob(job, trigger);
            Assert.True(await scheduler1.CheckExists(jobKey));

            await scheduler1.Shutdown(waitForJobsToComplete: false);
        }
        finally
        {
            await p1.DisposeAsync();
        }

        // Process 2: a fresh provider over the SAME container DB with VersionInfo cleared, so the migration
        // replays. The existence guard must find the UPPERCASE qrtz_job_details under CS collation and skip the
        // DDL — pre-fix the lowercase guard misses it and re-runs the DDL → "already an object named QRTZ_*".
        var p2 = BuildServices();
        try
        {
            await using (var scope = p2.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>()
                    .Database.ExecuteSqlRawAsync("DELETE FROM [dbo].[VersionInfo]");
            }

            LogContext.SetCurrentLogProvider(p2.GetRequiredService<ILoggerFactory>());
            await new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "cs-test" }).InitializeAsync(p2);

            // The durable job survived the replay and is readable through the rebuilt scheduler.
            var scheduler2 = await p2.GetRequiredService<ISchedulerFactory>().GetScheduler();
            Assert.True(await scheduler2.CheckExists(jobKey));
            await scheduler2.Shutdown(waitForJobsToComplete: false);
        }
        finally
        {
            await p2.DisposeAsync();
        }
    }

    private ServiceProvider BuildServices()
    {
        // Pooling=False so each disposed provider closes its SQL connections immediately. The test runs two
        // "processes" against one container; with pooling on, process 1's shut-down scheduler can leave a
        // pooled connection holding a lock that deadlocks process 2's migration replay.
        // Command Timeout=120: under parallel CI load the SQL Server container gets starved and Quartz's
        // schema-validation query can exceed the 30s default, surfacing as a misleading
        // "schema validation failed" (inner: "Execution Timeout Expired"). The longer timeout rides out
        // transient contention without masking a real schema problem.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = container.GetConnectionString() + ";Pooling=False;Command Timeout=120",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(DatabaseProviderNames.SqlServer));
        new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "cs-test" }).ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

/// <summary>
/// Startup fail-fast contract tests for <see cref="SchedulingModule.InitializeAsync"/> that need no database
/// — the guards throw before any connection is opened.
/// </summary>
public sealed class SchedulingModuleConfigurationTests
{
    [Fact]
    public async Task InitializeAsync_Throws_WhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(DatabaseProviderNames.Postgres));
        using var sp = services.BuildServiceProvider();

        var module = new SchedulingModule();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await module.InitializeAsync(sp));
    }

    [Fact]
    public async Task InitializeAsync_Throws_WhenProviderUnsupported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=unused" })
            .Build());
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider("mysql"));
        using var sp = services.BuildServiceProvider();

        var module = new SchedulingModule();

        // This exercises the module-level fail-fast in ToMigrationEngine, which throws before the runner is
        // ever invoked. The migration's own IfDatabase unsupported-engine guard is defense-in-depth, kept in
        // lockstep with ToMigrationEngine by the LOCKSTEP comment in SchedulingSchemaMigration; reaching it
        // would require a live unsupported-engine database, which is out of scope until EF MySQL exists.
        await Assert.ThrowsAsync<NotSupportedException>(async () => await module.InitializeAsync(sp));
    }

    [Fact]
    public void ConfigureServices_RegistersNoScheduler_WhenUsePersistentStoreFalse()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=unused" })
            .Build());
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(DatabaseProviderNames.Postgres));

        new SchedulingModule(new SchedulingModuleOptions { UsePersistentStore = false })
            .ConfigureServices(services);

        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<ISchedulerFactory>());
    }
}
