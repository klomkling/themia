using Microsoft.Extensions.DependencyInjection;

namespace Themia.Data.Migrations.SqlServer;

/// <summary>Registry entry point for the SQL Server migration engine adapter.</summary>
public static class SqlServerMigrationEngine
{
    /// <summary>The SQL Server <see cref="IMigrationEngineAdapter"/>.</summary>
    public static IMigrationEngineAdapter Adapter { get; } = new SqlServerMigrationEngineAdapter();

    /// <summary>
    /// Registers <see cref="Adapter"/> with <see cref="MigrationEngineRegistry"/> so
    /// <see cref="ThemiaMigrations.Run(MigrationEngine, string, System.Reflection.Assembly[])"/> can resolve
    /// <see cref="MigrationEngine.SqlServer"/>. Position-independent in startup — the registry is populated
    /// eagerly here regardless of where in <see cref="IServiceCollection"/> configuration this call appears.
    /// </summary>
    /// <param name="services">The service collection, for fluent chaining. Not otherwise used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThemiaDataMigrationsSqlServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        MigrationEngineRegistry.Add(Adapter);
        return services;
    }
}
