using Dapper;

using Npgsql;

using Themia.Data.Migrations;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(PostgresSequenceCollection.Name)]
public sealed class SequencesSchemaMigrationTests(PostgresSequenceFixture fixture)
{
    // Shares the fixture's container rather than starting its own — see the reasoning for that in
    // SequenceEngineFixtures.cs's remarks on this class. The fixture already ran the migration once in
    // its own InitializeAsync; every test method here reruns it deliberately to test the migration itself.
    private string ConnString => fixture.ConnectionString;

    [Fact]
    public async Task Migration_CreatesTheTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(ConnString);
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
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);

        await using (var conn = new NpgsqlConnection(ConnString))
        {
            await conn.ExecuteAsync("DELETE FROM themia_version_themia_framework_data_sequences");
        }

        // Must not throw: the table is already there and the ledger no longer remembers creating it.
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    [Fact]
    public async Task TenantId_IsNotNullable_SoItCanBePartOfThePrimaryKey()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(ConnString);
        var isNullable = await conn.ExecuteScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns "
            + "WHERE table_name = 'themia_sequences' AND column_name = 'tenant_id'");

        Assert.Equal("NO", isNullable);
    }
}
