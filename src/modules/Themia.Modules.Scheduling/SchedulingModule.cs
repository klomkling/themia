using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Core.Modules;
using Themia.Quartz;

namespace Themia.Modules.Scheduling;

/// <summary>
/// Themia module that wires the Quartz dashboard (<c>AddThemiaQuartz</c>), registers the
/// EF-backed <see cref="EfExecutionHistoryStore"/> as the <see cref="IExecutionHistoryStore"/>,
/// and creates/upgrades the scheduling schema on startup via EF Core migrations.
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
/// <b>DbContext lifetime.</b> <see cref="SchedulingDbContext"/> is registered as a singleton and the
/// store as a singleton, because the <c>MapThemiaQuartz</c> bridge resolves
/// <see cref="IExecutionHistoryStore"/> once from the root service provider. Execution-history writes
/// originate from the Quartz history plugin (low concurrency, serialized per scheduler), so a single
/// long-lived context is acceptable here; the store's increment paths use raw SQL to avoid tracked-entity
/// contention. Do not reuse this context for high-concurrency request work.
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
        version: new Version(0, 3, 0, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the scheduling DbContext as a singleton (see remarks on lifetime). Only if the
        // host has not already registered one, so an app that owns the context wins.
        services.TryAddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

            var dbOptions = new DbContextOptionsBuilder<SchedulingDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

            return new SchedulingDbContext(dbOptions);
        });

        var schedulerName = options.SchedulerName;
        services.TryAddSingleton<IExecutionHistoryStore>(sp =>
            new EfExecutionHistoryStore(
                sp.GetRequiredService<SchedulingDbContext>(),
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
    public override async ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Default dashboard authorization: allow any authenticated user. Themia has no claims/role model
    /// yet; hosts SHOULD override via <see cref="SchedulingModuleOptions.Authorize"/> with an admin check.
    /// </summary>
    private static Task<bool> DefaultAuthorize(HttpContext context) =>
        Task.FromResult(context.User.Identity?.IsAuthenticated == true);
}
