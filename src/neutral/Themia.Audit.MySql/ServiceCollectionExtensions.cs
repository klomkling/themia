using Microsoft.Extensions.DependencyInjection;

namespace Themia.Audit.MySql;

/// <summary>DI entry point for the MySQL-backed <c>Themia.Audit</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MySqlAuditDialect"/> as the <see cref="IAuditDialect"/> and
    /// <see cref="AuditStoreEngine"/> as the <see cref="IAuditStore"/>. Does not run
    /// <see cref="Migrations.AuditSchemaMigration"/> — apply it with
    /// <c>Themia.Data.Migrations.ThemiaMigrations.Run</c> during host startup.
    /// </summary>
    public static IServiceCollection AddThemiaAuditMySql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditDialect, MySqlAuditDialect>();
        services.AddSingleton<IAuditStore, AuditStoreEngine>();
        return services;
    }
}
