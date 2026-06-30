using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Migrations;
using Themia.Modules.Export.Store;

namespace Themia.Modules.Export;

/// <summary>The export module: asserts a Quartz scheduler is present, runs the FluentMigrator schema, and
/// registers the recurring cleanup job. The host wires the services via <c>AddThemiaExportModule(...)</c>;
/// this module exists for hosts that drive modules through the <see cref="IThemiaModule"/> convention.</summary>
public sealed class ExportModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly ExportModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    public ExportModule(MigrationEngine engine) : this(engine, new ExportModuleOptions()) { }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public ExportModule(MigrationEngine engine, ExportModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Export",
        displayName: "Export",
        description: "Asynchronous and scheduled tabular export with Storage delivery and completion notifications.",
        version: new Version(0, 7, 0, 0));

    /// <inheritdoc />
    /// <remarks>
    /// The schema migration runs synchronously via <c>ThemiaMigrations.Run</c> (FluentMigrator's runner is
    /// synchronous), so <paramref name="cancellationToken"/> is honored at the boundary but cannot interrupt
    /// an in-flight <c>MigrateUp</c>.
    /// </remarks>
    public override async ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        // Precondition: a Quartz scheduler must be available (Scheduling can be configured to register none).
        var schedulerFactory = sp.GetService<ISchedulerFactory>()
            ?? throw new InvalidOperationException(
                "Themia.Modules.Export requires a Quartz ISchedulerFactory; ensure the scheduling module registers a scheduler.");

        var configuration = sp.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the export module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(ExportSchemaMigration).Assembly);

        // Register the recurring cleanup job (idempotent: guard against double-scheduling).
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<CleanupJob>().WithIdentity("export-cleanup", "export").Build();
        var trigger = TriggerBuilder.Create()
            .WithIdentity("export-cleanup-trigger", "export")
            .WithCronSchedule(options.CleanupCron)
            .Build();
        if (!await scheduler.CheckExists(job.Key, cancellationToken).ConfigureAwait(false))
        {
            await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);
        }

        await ReconcileStaleRunsAsync(sp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles runs orphaned in <see cref="ExportRunStatus.Running"/> by a host restart: any run
    /// that started before the grace cutoff is marked Failed so it does not linger forever. The grace period
    /// leaves runs that are still actively executing (including on another instance) untouched.</summary>
    private async Task ReconcileStaleRunsAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var store = sp.GetRequiredService<IExportRunStore>();
        var logger = sp.GetService<ILogger<ExportModule>>() ?? (ILogger)NullLogger<ExportModule>.Instance;
        var cutoff = DateTimeOffset.UtcNow - options.StaleRunGracePeriod;

        var stale = await store.FindStaleRunningAcrossTenantsAsync(cutoff, cancellationToken).ConfigureAwait(false);
        foreach (var group in stale.GroupBy(r => r.TenantId))
        {
            using var _ = BackgroundTenantScope.Begin(group.Key);
            foreach (var run in group)
            {
                run.MarkFailed("Export was interrupted by a host restart and did not resume.", DateTimeOffset.UtcNow);
                try
                {
                    await store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to reconcile stale running export {RunId}.", run.Id);
                }
            }
        }
    }
}
