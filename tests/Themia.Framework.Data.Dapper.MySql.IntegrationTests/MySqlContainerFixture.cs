using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// Spins up a real MySQL container and creates the shared <c>widgets</c> table the Dapper provider maps to.
/// <see cref="ResetAsync"/> truncates between facts.
/// </summary>
public sealed class MySqlContainerFixture : IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4")
        .WithDatabase("themia_dapper_tests")
        .WithUsername("themia")
        .WithPassword("themia")
        .WithCleanUp(true)
        .Build();

    /// <summary>The connection string to the running container.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        await CreateSchemaAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Clears the shared table so each fact starts from an empty state.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE widgets";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS widgets (
                id                CHAR(36)      NOT NULL PRIMARY KEY,
                tenant_id         VARCHAR(100)  NULL,
                name              VARCHAR(200)  NOT NULL,
                quantity          INT           NOT NULL,
                created_at        DATETIME(6)   NOT NULL,
                created_by        VARCHAR(100)  NULL,
                last_modified_at  DATETIME(6)   NULL,
                last_modified_by  VARCHAR(100)  NULL,
                is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
                deleted_at        DATETIME(6)   NULL,
                deleted_by        VARCHAR(100)  NULL,
                restored_at       DATETIME(6)   NULL,
                restored_by       VARCHAR(100)  NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }
}
