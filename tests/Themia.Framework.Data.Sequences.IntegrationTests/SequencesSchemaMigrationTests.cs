using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SequencesSchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Migration_CreatesTheTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        var columns = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'themia_sequences'")).ToList();

        Assert.Contains("tenant_id", columns);
        Assert.Contains("sequence_key", columns);
        Assert.Contains("next_value", columns);
    }

    [Fact]
    public async Task Migration_IsReplaySafe()
    {
        // The per-assembly version ledger (coord #0078) starts EMPTY on every database that predates it,
        // so Up() runs against objects already there. Coord #0085 and #0096 are the outages this prevents:
        // an unguarded CREATE crash-loops the host at boot.
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(SequencesSchemaMigration).Assembly);

        await using (var conn = new NpgsqlConnection(connString))
        {
            await conn.ExecuteAsync("DELETE FROM themia_version_themia_framework_data_sequences");
        }

        // Must not throw: the table is already there and the ledger no longer remembers creating it.
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(SequencesSchemaMigration).Assembly);
    }

    [Fact]
    public async Task TenantId_IsNotNullable_SoItCanBePartOfThePrimaryKey()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        var isNullable = await conn.ExecuteScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns "
            + "WHERE table_name = 'themia_sequences' AND column_name = 'tenant_id'");

        Assert.Equal("NO", isNullable);
    }
}
