using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.Modules.Export.Tests;

/// <summary>Shared Testcontainers PostgreSQL fixture for the Export module integration tests
/// (Tasks 6–9). Starts one container per test class, runs the FluentMigrator schema migration once,
/// and exposes <see cref="NewContext"/> + <see cref="ResetAsync"/> for per-test data isolation.</summary>
public sealed class ExportDbFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        RunMigrations();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Creates a fresh <see cref="ExportDbContext"/> using snake_case naming conventions
    /// to match the FluentMigrator-owned schema. Callers are responsible for disposing.</summary>
    public ExportDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<ExportDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ExportDbContext(opts);
    }

    /// <summary>Truncates all export tables so each test fact starts from a clean state.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE export.export_runs, export.export_schedules CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private void RunMigrations()
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(container.GetConnectionString())
                .ScanIn(typeof(Migrations.ExportSchemaMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}

/// <summary>Test helpers for <see cref="Entities.ExportRun"/>.</summary>
internal static class ExportRunTestExtensions
{
    /// <summary>Sets the run's identity (for deterministic GUID test fixtures).</summary>
    internal static Entities.ExportRun WithId(this Entities.ExportRun run, Guid id)
    {
        run.SetId(id);
        return run;
    }
}
