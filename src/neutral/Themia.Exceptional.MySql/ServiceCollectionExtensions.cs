using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Exceptional;

namespace Themia.Exceptional.MySql;

/// <summary>DI entry point for the MySQL-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MySQL exception store: dialect, engine, options, and runs the
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
    /// <param name="connectionString">
    /// MySQL connection string. The dialect always pins <c>GuidFormat=Char36</c> on its own connections (the
    /// <c>Guid</c> column is <c>CHAR(36)</c>), so callers need not — and any conflicting <c>GuidFormat</c>/
    /// <c>OldGuids</c> is overridden to keep <see cref="System.Guid"/> lookups correct.
    /// </param>
    /// <param name="configure">
    /// Required configuration callback. <see cref="ExceptionalOptions.ApplicationName"/> is mandatory and
    /// validated at startup, so this cannot be omitted.
    /// </param>
    public static IServiceCollection AddThemiaExceptionalMySql(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddThemiaExceptionalProvider(
            dialect: new MySqlExceptionalDialect(connectionString),
            configure: configure,
            engine: MigrationEngine.MySql,
            connectionString: connectionString);
    }
}
