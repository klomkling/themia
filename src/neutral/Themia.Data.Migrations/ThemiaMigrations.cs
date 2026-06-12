using System.Reflection;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Data.Migrations;

/// <summary>
/// Neutral entry point that applies FluentMigrator migrations through the processor for a
/// chosen <see cref="MigrationEngine"/>. Shared by every Themia neutral core and framework module
/// so the per-engine runner wiring lives in exactly one place (DECISION #6: FluentMigrator is the
/// single schema authority).
/// </summary>
public static class ThemiaMigrations
{
    /// <summary>
    /// Applies all pending FluentMigrator migrations found in <paramref name="migrationAssemblies"/>
    /// against <paramref name="connectionString"/> using the <paramref name="engine"/>'s processor.
    /// Runs synchronously (<c>MigrateUp</c>).
    /// </summary>
    /// <param name="engine">The target database engine.</param>
    /// <param name="connectionString">Connection string for the migration runner. Required.</param>
    /// <param name="migrationAssemblies">One or more assemblies scanned for <c>[Migration]</c> types. At least one is required.</param>
    /// <exception cref="ArgumentException">The connection string is null/whitespace, or no assemblies were supplied.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="migrationAssemblies"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engine"/> is not a known engine.</exception>
    /// <exception cref="InvalidOperationException">The migration failed to apply; the message names the engine.</exception>
    public static void Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrationAssemblies);
        if (migrationAssemblies.Length == 0)
            throw new ArgumentException("At least one migration assembly is required.", nameof(migrationAssemblies));

        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                AddProcessor(rb, engine);
                rb.WithGlobalConnectionString(connectionString)
                  .ScanIn(migrationAssemblies).For.Migrations();
            })
            .BuildServiceProvider(false);

        using var scope = provider.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: failed to apply migrations against {DisplayName(engine)}. " +
                "Verify the connection string and that the principal has DDL permissions.", ex);
        }
    }

    private static void AddProcessor(IMigrationRunnerBuilder rb, MigrationEngine engine)
    {
        switch (engine)
        {
            case MigrationEngine.Postgres: rb.AddPostgres(); break;
            case MigrationEngine.MySql: rb.AddMySql8(); break;
            case MigrationEngine.SqlServer: rb.AddSqlServer(); break;
            default: throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static string DisplayName(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres => "PostgreSQL",
        MigrationEngine.MySql => "MySQL",
        MigrationEngine.SqlServer => "SQL Server",
        _ => engine.ToString(),
    };
}
