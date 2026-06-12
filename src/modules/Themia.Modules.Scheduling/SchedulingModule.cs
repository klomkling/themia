using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// The host owns the Quartz <c>IScheduler</c> and job registration; this module does not create one.
/// The dashboard resolves the scheduler from <see cref="ThemiaQuartzOptions.Scheduler"/> or a
/// DI-registered <c>IScheduler</c> at <c>MapThemiaQuartz</c> time.
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
        version: new Version(0, 4, 0, 0));

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
    }

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        using var scope = serviceProvider.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDatabaseProvider>();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

        ThemiaMigrations.Run(
            ToMigrationEngine(provider.ProviderName),
            connectionString,
            typeof(Migrations.SchedulingSchemaMigration).Assembly);

        return ValueTask.CompletedTask;
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
