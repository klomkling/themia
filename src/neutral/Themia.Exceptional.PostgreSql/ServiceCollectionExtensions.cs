using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
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

        return services.AddThemiaExceptionalProvider(
            dialect: new PostgresExceptionalDialect(connectionString),
            configure: configure,
            configureRunner: rb => rb.AddPostgres(),
            connectionString: connectionString,
            databaseDisplayName: "PostgreSQL");
    }
}
