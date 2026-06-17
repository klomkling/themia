using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Storage.Migrations;
using Xunit;

namespace Themia.Modules.Storage.IntegrationTests.Fixtures;

public sealed class PostgresStorageFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_storage_tests")
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
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(StorageSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE storage.storage_objects RESTART IDENTITY CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
