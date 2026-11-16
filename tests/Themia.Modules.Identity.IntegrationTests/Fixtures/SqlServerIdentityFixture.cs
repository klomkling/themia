using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests.Fixtures;

public sealed class SqlServerIdentityFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.SqlServer;

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
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var columns = new List<(string Table, string Column)>();
        await using (var catalog = connection.CreateCommand())
        {
            catalog.CommandText =
                "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = 'identity' AND DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar', 'text', 'ntext', 'xml');";
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
            select.CommandText = $"SELECT CAST([{column}] AS nvarchar(max)) FROM [identity].[{table}] WHERE [{column}] IS NOT NULL;";
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
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // 'IDENTITY' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        command.CommandText =
            "DELETE FROM [identity].external_logins; DELETE FROM [identity].session_second_factor; DELETE FROM [identity].refresh_tokens; DELETE FROM [identity].user_tokens; DELETE FROM [identity].user_claims; DELETE FROM [identity].role_claims; " +
            "DELETE FROM [identity].user_roles; DELETE FROM [identity].users; DELETE FROM [identity].roles;";
        await command.ExecuteNonQueryAsync();
    }
}
