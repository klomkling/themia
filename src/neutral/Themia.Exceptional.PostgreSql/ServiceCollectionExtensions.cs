using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Exceptional;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.Serilog;

namespace Themia.Exceptional.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL exception store: dialect, engine, options, and runs the
    /// FluentMigrator schema migration immediately so the <c>Exceptions</c> table exists.
    /// <para>
    /// Also registers <see cref="ExceptionalSerilogSink"/> and <see cref="HttpContextEnricher"/>
    /// as singletons in the DI container <strong>for the host to wire into its own Serilog
    /// <c>LoggerConfiguration</c></strong>. This package does not configure the global logger.
    /// The host should resolve and attach them, for example:
    /// <code>
    /// .Enrich.With(sp.GetRequiredService&lt;HttpContextEnricher&gt;())
    /// .WriteTo.Sink(sp.GetRequiredService&lt;ExceptionalSerilogSink&gt;())
    /// </code>
    /// </para>
    /// </summary>
    public static IServiceCollection AddThemiaExceptionalPostgres(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IExceptionalSqlDialect>(new PostgresExceptionalDialect(connectionString));
        services.AddThemiaExceptionalCore(o => configure?.Invoke(o));

        services.AddHttpContextAccessor();
        services.TryAddSingleton<HttpContextEnricher>();
        services.TryAddSingleton<ExceptionalSerilogSink>(sp =>
            new ExceptionalSerilogSink(
                sp.GetRequiredService<IExceptionStore>(),
                sp.GetRequiredService<ExceptionalOptions>()));

        RunMigration(connectionString);
        return services;
    }

    private static void RunMigration(string connectionString)
    {
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(ExceptionLogMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}
