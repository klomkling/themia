using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Themia.Data.Migrations;
using Themia.Data.Probes;
using Themia.Exceptional;

namespace Themia.Exceptional.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL exception store: dialect, engine, options, and runs the
    /// FluentMigrator schema migration immediately so the <c>Exceptions</c> table exists.
    /// <para>
    /// Also registers <see cref="Themia.Exceptional.Serilog.ExceptionalSerilogSink"/> and
    /// <see cref="Themia.Exceptional.Serilog.HttpContextEnricher"/>
    /// as singletons in the DI container <strong>for the host to wire into its own Serilog
    /// <c>LoggerConfiguration</c></strong>. This package does not configure the global logger.
    /// The host should resolve and attach them, for example:
    /// <code>
    /// .Enrich.With(sp.GetRequiredService&lt;HttpContextEnricher&gt;())
    /// .WriteTo.Sink(sp.GetRequiredService&lt;ExceptionalSerilogSink&gt;())
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="configure">
    /// Required configuration callback. <see cref="ExceptionalOptions.ApplicationName"/> is mandatory and
    /// validated at startup, so this cannot be omitted.
    /// </param>
    public static IServiceCollection AddThemiaExceptionalPostgres(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddThemiaExceptionalProvider(
            dialect: new PostgresExceptionalDialect(connectionString),
            configure: configure,
            engine: MigrationEngine.Postgres,
            connectionString: connectionString);

        // "Exceptions" is created quoted, so it must be probed quoted -- an unquoted probe folds to
        // lower case and would report a table that exists as missing.
        services.AddPostgresSchemaProbe(
            "Themia.Exceptional",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            ["\"Exceptions\""]);

        return services;
    }
}
