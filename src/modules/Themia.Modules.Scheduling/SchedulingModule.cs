using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Logging;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Quartz;

namespace Themia.Modules.Scheduling;

/// <summary>
/// Themia module that wires the Quartz dashboard (<c>AddThemiaQuartz</c>), registers the
/// EF-backed <see cref="EfExecutionHistoryStore"/> as the <see cref="IExecutionHistoryStore"/>,
/// and creates/upgrades the scheduling schema on startup via FluentMigrator (<c>ThemiaMigrations.Run</c>).
/// </summary>
/// <remarks>
/// <para>
/// By default (<see cref="SchedulingModuleOptions.UsePersistentStore"/> = <see langword="true"/>) the
/// module registers a persistent Quartz <c>IScheduler</c>/<c>ISchedulerFactory</c> backed by AdoJobStore
/// over the <c>quartz</c> schema, serialized with System.Text.Json, and starts it via the Quartz hosted
/// service. The <see cref="ExecutionHistoryPlugin"/> is attached as a job listener so executions are
/// recorded. Set <see cref="SchedulingModuleOptions.UsePersistentStore"/> = <see langword="false"/> to
/// register no scheduler — the host then supplies its own <c>IScheduler</c> (via
/// <see cref="ThemiaQuartzOptions.Scheduler"/> or a DI-registered <c>IScheduler</c>), as before. Either
/// way, the dashboard resolves the scheduler at <c>MapThemiaQuartz</c> time.
/// </para>
/// <para>
/// <b>Authorization.</b> Themia has no claims/role model yet, so the dashboard gate defaults to
/// <i>authenticated-only</i>. The dashboard is platform-admin surface; the host SHOULD tighten this
/// by supplying <see cref="SchedulingModuleOptions.Authorize"/> with an admin check before production.
/// </para>
/// <para>
/// <b>DbContext lifetime.</b> <see cref="SchedulingDbContext"/> is registered via
/// <c>AddDbContextFactory</c>, and <see cref="EfExecutionHistoryStore"/> is a singleton that creates
/// a short-lived context per operation. This makes concurrent Quartz listener callbacks (multiple
/// worker threads calling <c>Save</c>/<c>Increment*</c> simultaneously) safe — no shared
/// <c>DbContext</c> state exists between calls.
/// </para>
/// <para>
/// <b>Provider-agnostic (PostgreSQL + SQL Server).</b> The module selects the EF provider and the
/// FluentMigrator engine from the app's registered <see cref="IDatabaseProvider"/>; it requires one
/// (call <c>AddThemiaPostgres</c>/<c>AddThemiaSqlServer</c>). The store always uses the <c>Default</c>
/// connection — execution history is process-wide, never tenant-routed. MySQL arrives with the EF
/// MySQL provider.
/// </para>
/// </remarks>
public sealed class SchedulingModule : ThemiaModuleBase
{
    /// <summary>The configuration key for the scheduling database connection string.</summary>
    public const string ConnectionStringName = "Default";

    private readonly SchedulingModuleOptions options;

    /// <summary>Initializes the module with default options (authenticated-only dashboard access).</summary>
    public SchedulingModule()
        : this(new SchedulingModuleOptions())
    {
    }

    /// <summary>Initializes the module with explicit options.</summary>
    /// <param name="options">Module options controlling dashboard mount path, authorization, and scheduler name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SchedulingModule(SchedulingModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Scheduling",
        displayName: "Scheduling",
        description: "Quartz.NET scheduler dashboard with EF-backed execution history.",
        version: new Version(0, 4, 8, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the scheduling DbContext factory. Each operation in EfExecutionHistoryStore
        // creates a short-lived context via the factory, keeping concurrent Quartz callbacks safe.
        // Schema creation/upgrade is handled by ThemiaMigrations.Run in InitializeAsync, not here.
        services.AddDbContextFactory<SchedulingDbContext>((sp, dbOptions) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

            switch (provider.ProviderName)
            {
                case DatabaseProviderNames.Postgres:
                    dbOptions.UseNpgsql(connectionString);
                    break;
                case DatabaseProviderNames.SqlServer:
                    dbOptions.UseSqlServer(connectionString);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Themia.Scheduling supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported.");
            }

            dbOptions.UseSnakeCaseNamingConvention();
        });

        var schedulerName = options.SchedulerName;
        services.TryAddSingleton<IExecutionHistoryStore>(sp =>
            new EfExecutionHistoryStore(
                sp.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EfExecutionHistoryStore>>())
            {
                SchedulerName = schedulerName,
            });

        var virtualPathRoot = options.VirtualPathRoot;
        var authorize = options.Authorize ?? DefaultAuthorize;
        services.AddThemiaQuartz(o =>
        {
            o.VirtualPathRoot = virtualPathRoot;
            o.Authorize = authorize;
        });

        if (options.UsePersistentStore)
        {
            // Engine + Default connection are needed at registration time to wire the persistent store.
            // Read them from the already-registered singleton instances rather than building (and disposing)
            // a throwaway provider — disposing one tears down its ILoggerFactory, which Quartz captures
            // globally for logging, producing an ObjectDisposedException when the scheduler is later resolved.
            var provider = GetRegisteredInstance<IDatabaseProvider>(services)
                ?? throw new InvalidOperationException(
                    "An IDatabaseProvider must be registered (call AddThemiaPostgres/AddThemiaSqlServer) before " +
                    "the scheduling module; the persistent Quartz store selects its engine from it.");
            var configuration = GetRegisteredInstance<IConfiguration>(services)
                ?? throw new InvalidOperationException(
                    "An IConfiguration must be registered before the scheduling module.");
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");
            var providerName = provider.ProviderName;

            services.AddQuartz(q =>
            {
                q.SchedulerName = schedulerName;

                q.UsePersistentStore(s =>
                {
                    s.UseProperties = true;          // JobDataMap stored as string key-values
                    s.UseSystemTextJsonSerializer(); // no Newtonsoft (CLAUDE.md)

                    // qrtz_* tables live in the `quartz` schema → schema-qualified table prefix.
                    switch (providerName)
                    {
                        case DatabaseProviderNames.Postgres:
                            s.UsePostgres(ado =>
                            {
                                ado.ConnectionString = connectionString;
                                ado.TablePrefix = "quartz.qrtz_";
                            });
                            break;
                        case DatabaseProviderNames.SqlServer:
                            s.UseSqlServer(ado =>
                            {
                                ado.ConnectionString = connectionString;
                                // UPPERCASE QRTZ_ to match the verbatim Quartz SQL Server DDL (which creates
                                // [quartz].[QRTZ_*]). A case-insensitive collation forgives a lowercase prefix,
                                // but a case-sensitive collation does not — Quartz's runtime object references
                                // would then fail with "Invalid object name". The `quartz` schema is lowercase.
                                ado.TablePrefix = "quartz.QRTZ_";
                            });
                            break;
                        default:
                            throw new NotSupportedException(
                                $"Themia.Scheduling persistent Quartz supports PostgreSQL and SQL Server; provider '{providerName}' is not supported.");
                    }
                });

                // Themia owns the execution-history plugin now (was configured by the host's Quartz wiring).
                // Registered as a Quartz scheduler plugin (not a bare job listener) so its Initialize sets the
                // listener Name and self-registers as a job listener, and its Start wires the execution-history
                // store — the lifecycle the plugin is designed for. The DI-backed EF store is seeded onto the
                // scheduler context in InitializeAsync, before the hosted service starts the scheduler, so the
                // plugin reads it from there at Start().
                q.SetProperty(
                    "quartz.plugin.recentHistory.type",
                    $"{typeof(ExecutionHistoryPlugin).FullName}, {typeof(ExecutionHistoryPlugin).Assembly.GetName().Name}");
            });

            services.AddQuartzHostedService(h => h.WaitForJobsToComplete = true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The schema migration runs synchronously via <c>ThemiaMigrations.Run</c> (FluentMigrator's runner is
    /// synchronous), so <paramref name="cancellationToken"/> is honored at the boundary — observed before the
    /// migration starts — but cannot interrupt an in-flight <c>MigrateUp</c>.
    /// <para>
    /// When <see cref="SchedulingModuleOptions.UsePersistentStore"/> is enabled, after the migration this also
    /// seeds the EF-backed <see cref="IExecutionHistoryStore"/> onto the scheduler context. This runs before the
    /// Quartz hosted service starts the scheduler, so <see cref="ExecutionHistoryPlugin"/>'s <c>Start</c> caches
    /// the EF store rather than its in-proc fallback — avoiding the split-brain where execution history would be
    /// silently written to a discarded in-memory store.
    /// </para>
    /// </remarks>
    public override async ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        string connectionString;
        IDatabaseProvider provider;
        using (var scope = serviceProvider.CreateScope())
        {
            provider = scope.ServiceProvider.GetRequiredService<IDatabaseProvider>();
            connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");
        }

        ThemiaMigrations.Run(
            ToMigrationEngine(provider.ProviderName),
            connectionString,
            typeof(Migrations.SchedulingSchemaMigration).Assembly);

        if (options.UsePersistentStore)
        {
            // Building the scheduler here (before the hosted service) reads Quartz's process-global LogContext.
            // Point it at this app's ILoggerFactory so Quartz logs through DI (its default is console) and never
            // captures a stale/disposed provider. Quartz owns exactly one LogContext per process, so this is the
            // app's logging wiring rather than a per-instance override. If GetScheduler below throws, the global
            // keeps referencing serviceProvider's ILoggerFactory — benign, as serviceProvider is the host's
            // process-lifetime root, not a throwaway, so the factory is not disposed out from under it.
            LogContext.SetCurrentLogProvider(serviceProvider.GetRequiredService<ILoggerFactory>());

            // Seed the execution-history store onto the scheduler context BEFORE the hosted service starts the
            // scheduler, so ExecutionHistoryPlugin.Start() caches the EF store rather than the in-proc fallback.
            // GetScheduler() builds the scheduler (the qrtz_* tables created above must already exist); the
            // hosted service later reuses and starts this same instance.
            var schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>();
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var historyStore = serviceProvider.GetRequiredService<IExecutionHistoryStore>();
            scheduler.Context.SetExecutionHistoryStore(historyStore);
        }
    }

    // Reads a singleton instance registered via AddSingleton(instance)/TryAddSingleton(instance) straight
    // from the service descriptors, so registration-time wiring need not build a service provider. Both
    // IDatabaseProvider (AddThemiaPostgres/SqlServer) and IConfiguration are registered as instances.
    private static T? GetRegisteredInstance<T>(IServiceCollection services) where T : class
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(T) && descriptor.ImplementationInstance is T instance)
            {
                return instance;
            }
        }

        return null;
    }

    private static MigrationEngine ToMigrationEngine(string providerName) => providerName switch
    {
        DatabaseProviderNames.Postgres => MigrationEngine.Postgres,
        DatabaseProviderNames.SqlServer => MigrationEngine.SqlServer,
        _ => throw new NotSupportedException(
            $"Themia.Scheduling supports PostgreSQL and SQL Server; provider '{providerName}' is not supported."),
    };

    /// <summary>
    /// Default dashboard authorization: allow any authenticated user. Themia has no claims/role model
    /// yet; hosts SHOULD override via <see cref="SchedulingModuleOptions.Authorize"/> with an admin check.
    /// </summary>
    private static Task<bool> DefaultAuthorize(HttpContext context) =>
        Task.FromResult(context.User.Identity?.IsAuthenticated == true);
}
