using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Exceptions;
using FluentMigrator.Runner.Initialization;
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
    /// <param name="migrationAssemblies">
    /// One or more assemblies scanned for <c>[Migration]</c> types. At least one is required, and the
    /// supplied set must contain at least one migration — passing assemblies with no <c>[Migration]</c>
    /// types is rejected rather than silently applying nothing.
    /// </param>
    /// <exception cref="ArgumentException">The connection string is null/whitespace, no assemblies were supplied, or the assemblies contain no <c>[Migration]</c> types.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="migrationAssemblies"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engine"/> is not a known engine.</exception>
    /// <exception cref="InvalidOperationException">The migration failed to apply; the message names the engine.</exception>
    public static void Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrationAssemblies);
        if (migrationAssemblies.Length == 0)
            throw new ArgumentException("At least one migration assembly is required.", nameof(migrationAssemblies));

        // One source of truth for per-engine knowledge (processor + display name). Resolved up front so an
        // unknown engine fails as a clean guard before any infrastructure is built.
        var (addProcessor, displayName) = Describe(engine);

        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                addProcessor(rb);
                rb.WithGlobalConnectionString(connectionString)
                  .ScanIn(migrationAssemblies).For.Migrations();
            })
            .BuildServiceProvider(false);

        using var scope = provider.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // Fail fast if the supplied assemblies carry no migrations: scanning happens in memory (no DB
        // connection), so a wrong/empty assembly is caught before MigrateUp would silently no-op and leave
        // the schema uncreated. This is independent of applied state, so idempotent re-runs still pass.
        if (!HasMigrations(serviceProvider))
            throw new ArgumentException(
                "The supplied assemblies contain no FluentMigrator [Migration] types; nothing would be applied.",
                nameof(migrationAssemblies));

        try
        {
            serviceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: failed to apply migrations against {displayName}. " +
                "Verify the connection string and that the principal has DDL permissions.", ex);
        }
    }

    private static bool HasMigrations(IServiceProvider serviceProvider)
    {
        var loader = serviceProvider.GetRequiredService<IMigrationInformationLoader>();
        try
        {
            return loader.LoadMigrations().Count > 0;
        }
        catch (MissingMigrationsException)
        {
            return false;
        }
    }

    private static (Action<IMigrationRunnerBuilder> AddProcessor, string DisplayName) Describe(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres => (rb => rb.AddPostgres(), "PostgreSQL"),
        MigrationEngine.MySql => (rb => rb.AddMySql8(), "MySQL"),
        MigrationEngine.SqlServer => (rb => rb.AddSqlServer(), "SQL Server"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine."),
    };
}
