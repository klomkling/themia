using Microsoft.Extensions.DependencyInjection;

namespace Themia.Data.Migrations.PostgreSql;

/// <summary>Registry entry point for the PostgreSQL migration engine adapter.</summary>
public static class PostgresMigrationEngine
{
    /// <summary>The PostgreSQL <see cref="IMigrationEngineAdapter"/>.</summary>
    public static IMigrationEngineAdapter Adapter { get; } = new PostgresMigrationEngineAdapter();

    /// <summary>
    /// Registers <see cref="Adapter"/> with <see cref="MigrationEngineRegistry"/> so
    /// <see cref="ThemiaMigrations.Run(MigrationEngine, string, System.Reflection.Assembly[])"/> can resolve
    /// <see cref="MigrationEngine.Postgres"/>. Position-independent in startup — the registry is populated
    /// eagerly here regardless of where in <see cref="IServiceCollection"/> configuration this call appears.
    /// </summary>
    /// <param name="services">The service collection, for fluent chaining. Not otherwise used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThemiaDataMigrationsPostgreSql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        MigrationEngineRegistry.Add(Adapter);
        return services;
    }
}
