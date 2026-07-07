using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Pdf;
using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI entry points for the PDF module.</summary>
public static class PdfModuleServiceCollectionExtensions
{
    /// <summary>Registers the PDF module on the EF Core data peer (SQL Server / PostgreSQL): the
    /// <see cref="PdfDbContext"/> factory, <see cref="EfPdfTemplateStore"/>, and the scoped renderer.
    /// The app must also register <see cref="IDatabaseProvider"/>, <see cref="Themia.Framework.Data.Abstractions.Exceptions.ISqlExceptionInterpreter"/>,
    /// and <see cref="Themia.Framework.Core.Abstractions.Tenancy.ITenantContext"/> (typically via the EF Core data layer / host); this
    /// method does not re-register those framework-owned services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to tune <see cref="PdfModuleOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaPdfModuleEfCore(
        this IServiceCollection services, Action<PdfModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCommon(services, configure);

        services.AddDbContextFactory<PdfDbContext>((sp, db) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<PdfModuleOptions>>().Value;
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{options.ConnectionStringName}' was not found; the PDF module requires it.");
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
                        $"Themia.Modules.Pdf on EF Core supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported. Use the Dapper peer for MySQL.");
            }

            db.UseSnakeCaseNamingConvention();
        });
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<PdfDbContext>>().CreateDbContext());
        services.TryAddScoped<IPdfTemplateStore, EfPdfTemplateStore>();
        return services;
    }

    /// <summary>Registers the PDF module on the Dapper data peer (SQL Server / PostgreSQL / MySQL):
    /// <see cref="DapperPdfTemplateStore"/> and the scoped renderer. The app must also register the
    /// framework Dapper layer (<c>AddThemiaDapperCore</c>) — which supplies <c>ITenantQueryFactory</c>,
    /// <c>ISqlCompiler</c>, <c>IDapperConnectionContext</c>, <c>IRepository&lt;PdfTemplate, Guid&gt;</c>,
    /// and <c>IUnitOfWork</c> — plus an engine provider; this method does not register any of those.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to tune <see cref="PdfModuleOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaPdfModuleDapper(
        this IServiceCollection services, Action<PdfModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCommon(services, configure);
        services.TryAddScoped<IPdfTemplateStore, DapperPdfTemplateStore>();
        return services;
    }

    private static void AddCommon(IServiceCollection services, Action<PdfModuleOptions>? configure)
    {
        var optionsBuilder = services.AddOptions<PdfModuleOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionStringName),
                "PdfModuleOptions.ConnectionStringName must be set.")
            .ValidateOnStart();

        services.AddThemiaPdf(); // neutral renderers (singletons)
        services.TryAddScoped<IDataFilterScope, DataFilterScope>();
        services.TryAddScoped<IPdfDocumentRenderer, PdfDocumentRenderer>();
    }
}
