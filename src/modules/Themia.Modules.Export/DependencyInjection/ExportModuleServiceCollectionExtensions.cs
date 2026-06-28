using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Export.DependencyInjection;
using Themia.Export.Excel.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;

namespace Themia.Modules.Export.DependencyInjection;

/// <summary>DI entry point for the export module.</summary>
public static class ExportModuleServiceCollectionExtensions
{
    /// <summary>Registers the export module services: the CSV/Excel writers, the definitions registry,
    /// the EF stores, the request/schedule services, and the Quartz jobs.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to tune <see cref="ExportModuleOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaExportModule(
        this IServiceCollection services, Action<ExportModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddThemiaExport();
        services.AddThemiaExcelExport();
        services.TryAddSingleton<IDataFilterScope, DataFilterScope>();

        // Background jobs run request-less; this surfaces the BackgroundTenantScope's ambient tenant to
        // the DbContext and storage. TryAdd lets a host's accessor-reading context (e.g. AspNetCore) win.
        services.TryAddScoped<ITenantContext, AmbientTenantContext>();

        services.AddDbContextFactory<ExportDbContext>((sp, db) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<ExportModuleOptions>>().Value;
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{options.ConnectionStringName}' was not found; the export module requires it.");

            switch (provider.ProviderName)
            {
                case DatabaseProviderNames.Postgres:
                    db.UseNpgsql(connectionString);
                    break;
                case DatabaseProviderNames.SqlServer:
                    db.UseSqlServer(connectionString);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Themia.Export supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported.");
            }

            db.UseSnakeCaseNamingConvention();
        });

        services.TryAddScoped<ExportDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ExportDbContext>>().CreateDbContext());

        services.TryAddSingleton<IExportDefinitionRegistry, ExportDefinitionRegistry>();
        services.TryAddScoped<IExportRunStore, ExportRunStore>();
        services.TryAddScoped<IExportScheduleStore, ExportScheduleStore>();

        // ExportRequestService implements both seams; register once and alias both interfaces to the same
        // scoped instance so they share the scoped tenant context.
        services.TryAddScoped<ExportRequestService>();
        services.TryAddScoped<IExportRequestService>(sp => sp.GetRequiredService<ExportRequestService>());
        services.TryAddScoped<IExportRunEnqueuer>(sp => sp.GetRequiredService<ExportRequestService>());

        services.AddTransient<ExportJob>();
        services.AddTransient<ExportScheduleJob>();
        services.AddTransient<CleanupJob>();
        return services;
    }
}
