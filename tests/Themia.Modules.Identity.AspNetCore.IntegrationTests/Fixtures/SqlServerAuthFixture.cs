using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests.Fixtures;

/// <summary>Starts a SQL Server Testcontainer, runs Identity migrations, and exposes a reset helper.</summary>
public sealed class SqlServerAuthFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, ConnectionString, typeof(IdentitySchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // 'IDENTITY' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        command.CommandText =
            "DELETE FROM [identity].refresh_tokens; DELETE FROM [identity].user_tokens; DELETE FROM [identity].user_claims; DELETE FROM [identity].role_claims; " +
            "DELETE FROM [identity].user_roles; DELETE FROM [identity].users; DELETE FROM [identity].roles;";
        await command.ExecuteNonQueryAsync();
    }
}
