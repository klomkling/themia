using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Content.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SqlServerContentDialect"/> as the <see cref="IContentPageDialect"/> and runs
    /// <see cref="ContentSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaContent</c> as well, in either order. Content shipped with application code must go
    /// through <c>IContentPageService.SaveAsync</c> after startup, never through SQL (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static IServiceCollection AddThemiaContentSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new SqlServerContentDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);

        ThemiaMigrations.Run(SqlServerMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        return services;
    }
}
