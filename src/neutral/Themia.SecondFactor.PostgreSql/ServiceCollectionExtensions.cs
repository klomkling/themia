using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Probes;
using Themia.SecondFactor.PostgreSql.Migrations;

namespace Themia.SecondFactor.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.SecondFactor</c> stores (PostgreSQL 15+).</summary>
public static class ServiceCollectionExtensions
{
    private static readonly string[] Tables =
    [
        "second_factor_methods",
        "second_factor_recovery_codes",
        "second_factor_user_state",
        "second_factor_webauthn_ceremonies",
        "second_factor_audit",
    ];

    /// <summary>
    /// Registers the method, recovery-code, user-state, ceremony and audit stores and runs the schema migration
    /// immediately so the tables exist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static IServiceCollection AddThemiaSecondFactorPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        ThemiaMigrations.Run(PostgresMigrationEngine.Adapter, connectionString, typeof(SecondFactorSchemaMigration).Assembly);

        services.TryAddSingleton(new SecondFactorDataSource(connectionString));
        services.TryAddSingleton<IMfaMethodStore, PostgresMfaMethodStore>();
        services.TryAddSingleton<IMfaRecoveryCodeStore, PostgresMfaRecoveryCodeStore>();
        services.TryAddSingleton<IMfaUserStateStore, PostgresMfaUserStateStore>();
        services.TryAddSingleton<IMfaCeremonyStore, PostgresMfaCeremonyStore>();
        services.TryAddSingleton<IMfaAuditLog, PostgresMfaAuditLog>();
        services.TryAddSingleton<IMfaResetStore, PostgresMfaResetStore>();
        services.AddHostedService<CeremonyPurgeService>();

        services.AddPostgresSchemaProbe(
            "Themia.SecondFactor",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            Tables);

        return services;
    }
}
