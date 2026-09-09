using Microsoft.Extensions.DependencyInjection;
using Themia.Audit.DependencyInjection;
using Themia.Data.Migrations.PostgreSql;

namespace Themia.Audit.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Audit</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAuditDialect"/> as the <see cref="IAuditDialect"/> and
    /// <see cref="AuditStoreEngine"/> as the <see cref="IAuditStore"/>. Also records this engine's
    /// <c>IMigrationEngineAdapter</c> with <c>AddThemiaAudit</c>'s order-free <c>runMigration</c>
    /// handshake (design §5.2): the migration itself runs from whichever of this call and
    /// <c>AddThemiaAudit</c> completes the pair, once, and only when <c>runMigration</c> was requested.
    /// </summary>
    public static IServiceCollection AddThemiaAuditPostgreSql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditDialect, PostgresAuditDialect>();
        services.AddSingleton<IAuditStore, AuditStoreEngine>();
        AuditMigrationHandshake.RecordAdapter(services, PostgresMigrationEngine.Adapter);
        return services;
    }
}
