using Microsoft.Extensions.DependencyInjection;

namespace Themia.Audit.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Audit</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerAuditDialect"/> as the <see cref="IAuditDialect"/> and
    /// <see cref="AuditStoreEngine"/> as the <see cref="IAuditStore"/>. Does not run
    /// <see cref="Migrations.AuditSchemaMigration"/> — apply it with
    /// <c>Themia.Data.Migrations.ThemiaMigrations.Run</c> during host startup.
    /// </summary>
    public static IServiceCollection AddThemiaAuditSqlServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditDialect, SqlServerAuditDialect>();
        services.AddSingleton<IAuditStore, AuditStoreEngine>();
        return services;
    }
}
