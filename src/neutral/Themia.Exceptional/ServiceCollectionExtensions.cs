using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Data.Migrations;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.Serilog;

namespace Themia.Exceptional;

/// <summary>Shared registration used by provider packages (e.g. Themia.Exceptional.PostgreSql).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exception store over a provider-supplied <see cref="IExceptionalSqlDialect"/> plus validated options.
    /// Provider packages call this after registering their dialect.
    /// </summary>
    public static IServiceCollection AddThemiaExceptionalCore(this IServiceCollection services, Action<ExceptionalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ExceptionalOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton<IExceptionStore>(sp =>
            new ExceptionStoreEngine(sp.GetRequiredService<IExceptionalSqlDialect>(), options));

        return services;
    }

    /// <summary>
    /// Registers a complete provider-backed exception store: the <paramref name="dialect"/>, the core
    /// engine/options, the Serilog sink + HTTP-context enricher singletons, and (unless
    /// <paramref name="runMigration"/> is <see langword="false"/>) runs the FluentMigrator schema migration
    /// immediately so the <c>Exceptions</c> table exists.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dialect">The provider dialect (already carries its connection string).</param>
    /// <param name="configure">Required options callback; <see cref="ExceptionalOptions.ApplicationName"/> is validated at startup.</param>
    /// <param name="engine">
    /// The database engine whose FluentMigrator processor applies the schema migration. This <strong>must</strong>
    /// correspond to the <paramref name="dialect"/>'s engine — they are supplied independently and are not
    /// cross-checked, so a mismatch (e.g. <see cref="MigrationEngine.MySql"/> with a PostgreSQL dialect) would run
    /// the wrong processor. Prefer the per-engine entry points (<c>AddThemiaExceptionalPostgres</c>/<c>…MySql</c>/
    /// <c>…SqlServer</c>), which pair the dialect and engine for you; call this overload directly only when backing
    /// a custom dialect.
    /// </param>
    /// <param name="connectionString">Connection string passed to the migration runner. Required when <paramref name="runMigration"/> is <see langword="true"/>.</param>
    /// <param name="runMigration">When <see langword="true"/> (default), runs the schema migration immediately.</param>
    public static IServiceCollection AddThemiaExceptionalProvider(
        this IServiceCollection services,
        IExceptionalSqlDialect dialect,
        Action<ExceptionalOptions> configure,
        MigrationEngine engine,
        string? connectionString = null,
        bool runMigration = true)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IExceptionalSqlDialect>(dialect);
        services.AddThemiaExceptionalCore(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<HttpContextEnricher>();
        services.TryAddSingleton<ExceptionalSerilogSink>(sp =>
            new ExceptionalSerilogSink(
                sp.GetRequiredService<IExceptionStore>(),
                sp.GetRequiredService<ExceptionalOptions>()));

        if (runMigration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ThemiaMigrations.Run(engine, connectionString, typeof(ExceptionLogMigration).Assembly);
        }

        return services;
    }
}
