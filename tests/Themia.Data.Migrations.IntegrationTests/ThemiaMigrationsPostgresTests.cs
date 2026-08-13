using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.IntegrationTests;

[Trait("Category", "Integration")]
public class ThemiaMigrationsPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Run_CreatesTheMigratedTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly);

        Assert.True(await TableExistsAsync("migrations_probe"));
    }

    [Fact]
    public void Run_IsIdempotent_WhenInvokedTwice()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly);
        var second = Record.Exception(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly));

        Assert.Null(second);
    }

    [Fact]
    public async Task Run_AppliesEachMigrationOnce_WhenInstancesBootSimultaneously()
    {
        // The scenario the boot lock exists for: several instances start together, all see the same
        // migration pending, and all call Run. Without serialization they collide on the DDL and duplicate
        // the version row; with it, one applies and the rest find the work already done.
        //
        // The ledger is per assembly since coord #0078 — Themia no longer writes FluentMigrator's shared
        // VersionInfo — so the count comes from this assembly's own table, derived rather than spelled
        // out so a rename cannot leave this asserting against a table nothing writes.
        const int Instances = 6;

        var boots = Enumerable.Range(0, Instances).Select(_ => Task.Run(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly)));

        await Task.WhenAll(boots);

        Assert.True(await TableExistsAsync("migrations_probe"));
        var ledger = new ThemiaVersionTable(typeof(ProbeMigration).Assembly).TableName;
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM \"{ledger}\""));
    }

    [Fact]
    public void Run_WrapsFailure_NamingTheEngine()
    {
        const string badConn = "Host=127.0.0.1;Port=1;Username=x;Password=y;Database=z;Timeout=2;Command Timeout=2";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, badConn, typeof(ProbeMigration).Assembly));

        Assert.Contains("PostgreSQL", ex.Message);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", table);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
