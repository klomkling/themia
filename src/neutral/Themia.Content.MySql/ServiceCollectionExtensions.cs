using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;

namespace Themia.Content.MySql;

/// <summary>DI entry point for the MySQL-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="MySqlContentDialect"/> as the <see cref="IContentPageDialect"/> and runs
    /// <see cref="ContentSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaContent</c> as well, in either order. Content shipped with application code must go
    /// through <c>IContentPageService.SaveAsync</c> after startup, never through SQL (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">MySQL connection string.</param>
    public static IServiceCollection AddThemiaContentMySql(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new MySqlContentDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);

        ThemiaMigrations.Run(MySqlMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        return services;
    }
}
