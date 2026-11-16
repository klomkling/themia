using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests.Fixtures;

public sealed class PostgresIdentityFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_identity_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.Postgres;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(IdentitySchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Every value of every text-like column of every table in the identity schema, found through the catalog so a table or column added later is covered.</summary>
    public async Task<IReadOnlyList<string>> ReadAllTextAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var columns = new List<(string Table, string Column)>();
        await using (var catalog = connection.CreateCommand())
        {
            catalog.CommandText =
                "SELECT table_name, column_name FROM information_schema.columns " +
                "WHERE table_schema = 'identity' AND data_type IN ('text', 'character varying', 'character', 'json', 'jsonb');";
            await using var reader = await catalog.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var values = new List<string>();
        foreach (var (table, column) in columns)
        {
            await using var select = connection.CreateCommand();
            select.CommandText = $"SELECT \"{column}\"::text FROM identity.\"{table}\" WHERE \"{column}\" IS NOT NULL;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "TRUNCATE identity.external_logins, identity.refresh_tokens, identity.user_tokens, identity.user_claims, " +
            "identity.role_claims, identity.user_roles, identity.users, identity.roles RESTART IDENTITY CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
