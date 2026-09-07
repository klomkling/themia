using Microsoft.Extensions.DependencyInjection;

namespace Themia.Audit.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Audit</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAuditDialect"/> as the <see cref="IAuditDialect"/> and
    /// <see cref="AuditStoreEngine"/> as the <see cref="IAuditStore"/>. Does not run
    /// <see cref="Migrations.AuditSchemaMigration"/> — apply it with
    /// <c>Themia.Data.Migrations.ThemiaMigrations.Run</c> during host startup.
    /// </summary>
    public static IServiceCollection AddThemiaAuditPostgreSql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditDialect, PostgresAuditDialect>();
        services.AddSingleton<IAuditStore, AuditStoreEngine>();
        return services;
    }
}
