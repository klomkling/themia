using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Modules.Storage.Migrations;
using Xunit;

namespace Themia.Modules.Storage.IntegrationTests.Fixtures;

public sealed class SqlServerStorageFixture : IAsyncLifetime
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
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(StorageSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // 'storage' is not a reserved keyword in SQL Server — the schema qualifier needs no brackets.
        command.CommandText = "DELETE FROM storage.storage_objects;";
        await command.ExecuteNonQueryAsync();
    }
}
