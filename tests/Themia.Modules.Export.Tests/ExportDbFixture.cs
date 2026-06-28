using System.Collections.Specialized;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quartz;
using Quartz.Impl;
using Testcontainers.PostgreSql;
using Themia.Export;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

/// <summary>Shared Testcontainers PostgreSQL fixture for the Export module integration tests
/// (Tasks 6–9). Starts one container per test class, runs the FluentMigrator schema migration once,
/// and exposes <see cref="NewContext"/> + <see cref="ResetAsync"/> for per-test data isolation.</summary>
public sealed class ExportDbFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        RunMigrations();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Creates a fresh <see cref="ExportDbContext"/> using snake_case naming conventions
    /// to match the FluentMigrator-owned schema. Callers are responsible for disposing.</summary>
    public ExportDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<ExportDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ExportDbContext(opts);
    }

    /// <summary>Truncates all export tables so each test fact starts from a clean state.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE export.export_runs, export.export_schedules CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Creates a fresh, isolated in-memory Quartz scheduler (RAMJobStore). Each call gets a
    /// unique instance name so test facts never share scheduled jobs. The scheduler is not started, so
    /// scheduled jobs remain dormant — tests assert scheduling, not execution.</summary>
    public async Task<IScheduler> NewMemoryScheduler()
    {
        var props = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"export-test-{Guid.NewGuid():N}",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
            ["quartz.threadPool.threadCount"] = "1",
        };
        var factory = new StdSchedulerFactory(props);
        return await factory.GetScheduler();
    }

    /// <summary>Wires an <see cref="IExportRequestService"/> over a fresh context: run + schedule stores,
    /// a registry of stub definitions for the given keys (all disallowing soft-delete), a factory that
    /// returns <paramref name="scheduler"/>, and a fixed tenant context.</summary>
    public IExportRequestService BuildRequestService(
        IScheduler scheduler, TenantId tenant, IReadOnlyList<string> definitions)
    {
        var ctx = NewContext();
        var filterScope = new DataFilterScope();
        var runStore = new ExportRunStore(ctx, filterScope);
        var scheduleStore = new ExportScheduleStore(ctx, filterScope);
        var registry = new ExportDefinitionRegistry(
            definitions.Select(k => (IExportDefinition)new StubRequestDefinition(k)).ToList());
        var schedulerFactory = new SingleSchedulerFactory(scheduler);
        var tenantContext = new TenantContext(tenant);
        return new ExportRequestService(runStore, scheduleStore, registry, schedulerFactory, tenantContext);
    }

    private void RunMigrations()
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(container.GetConnectionString())
                .ScanIn(typeof(Migrations.ExportSchemaMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}

/// <summary>An <see cref="ISchedulerFactory"/> that always hands back a single, pre-built scheduler.</summary>
internal sealed class SingleSchedulerFactory(IScheduler scheduler) : ISchedulerFactory
{
    public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default)
        => Task.FromResult(scheduler);

    public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default)
        => Task.FromResult<IScheduler?>(scheduler);

    public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IScheduler>>([scheduler]);
}

/// <summary>A keyed export definition that disallows soft-delete (drives the request-service guard tests).</summary>
internal sealed class StubRequestDefinition(string key) : IExportDefinition
{
    public string Key => key;
    public bool AllowsIncludeSoftDeleted => false;

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ExportResult([1, 2, 3], "text/csv", "export.csv"));
}

/// <summary>Test helpers for <see cref="Entities.ExportRun"/>.</summary>
internal static class ExportRunTestExtensions
{
    /// <summary>Sets the run's identity (for deterministic GUID test fixtures).</summary>
    internal static Entities.ExportRun WithId(this Entities.ExportRun run, Guid id)
    {
        run.SetId(id);
        return run;
    }
}
