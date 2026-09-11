using Microsoft.Extensions.DependencyInjection;
using Themia.Audit.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Audit.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Audit</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerAuditDialect"/> as the <see cref="IAuditDialect"/> and
    /// <see cref="AuditStoreEngine"/> as the <see cref="IAuditStore"/>. Also records this engine's
    /// <c>IMigrationEngineAdapter</c> with <c>AddThemiaAudit</c>'s order-free <c>runMigration</c>
    /// handshake (design §5.2): the migration itself runs from whichever of this call and
    /// <c>AddThemiaAudit</c> completes the pair, once, and only when <c>runMigration</c> was requested.
    /// </summary>
    public static IServiceCollection AddThemiaAuditSqlServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditDialect, SqlServerAuditDialect>();
        services.AddSingleton<IAuditStore, AuditStoreEngine>();
        // Register on use, so an adopter who calls only engine-specific methods still has the enum path
        // (a module, SchedulingSchema.Migrate) resolvable — see MigrationEngineRegistry.Add (coord #0126).
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);

        AuditMigrationHandshake.RecordAdapter(services, SqlServerMigrationEngine.Adapter);
        return services;
    }
}
