using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Probes;

namespace Themia.Content.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresContentDialect"/> as the <see cref="IContentPageDialect"/>, runs
    /// <see cref="ContentSchemaMigration"/> immediately, and adds a startup probe that fails the host when the two
    /// tables are not on the connection's <c>search_path</c>.
    /// </summary>
    /// <remarks>Call <see cref="Themia.Content.DependencyInjection.ContentServiceCollectionExtensions.AddThemiaContent"/>
    /// as well, in either order. Content shipped with application code must go through
    /// <see cref="Themia.Content.IContentPageService.SaveAsync"/> after startup — never through a migration that
    /// writes these tables (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static IServiceCollection AddThemiaContentPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new PostgresContentDialect(connectionString));

        // Register on use, so the enum migration path stays resolvable for an adopter who calls only
        // engine-specific methods (coord #0126).
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

        ThemiaMigrations.Run(PostgresMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        services.AddPostgresSchemaProbe(
            "Themia.Content",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            ["content_pages", "content_page_revisions"]);

        return services;
    }
}
