using Microsoft.Extensions.DependencyInjection;

namespace Themia.Data.Migrations.MySql;

/// <summary>Registry entry point for the MySQL migration engine adapter.</summary>
public static class MySqlMigrationEngine
{
    /// <summary>The MySQL <see cref="IMigrationEngineAdapter"/>.</summary>
    public static IMigrationEngineAdapter Adapter { get; } = new MySqlMigrationEngineAdapter();

    /// <summary>
    /// Registers <see cref="Adapter"/> with <see cref="MigrationEngineRegistry"/> so
    /// <see cref="ThemiaMigrations.Run(MigrationEngine, string, System.Reflection.Assembly[])"/> can resolve
    /// <see cref="MigrationEngine.MySql"/>. Position-independent in startup — the registry is populated
    /// eagerly here regardless of where in <see cref="IServiceCollection"/> configuration this call appears.
    /// </summary>
    /// <param name="services">The service collection, for fluent chaining. Not otherwise used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThemiaDataMigrationsMySql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        MigrationEngineRegistry.Add(Adapter);
        return services;
    }
}
