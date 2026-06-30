using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportSchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Migration_creates_export_tables()
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(container.GetConnectionString())
                .ScanIn(typeof(Migrations.ExportSchemaMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp(); // throws on failure — this is the RED gate

        Assert.True(await TableExistsAsync("export_runs"));
        Assert.True(await TableExistsAsync("export_schedules"));
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", $"export.{table}");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
