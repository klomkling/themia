using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>
/// Spins up a real PostgreSQL container and creates the single <c>widgets</c> table that BOTH the Dapper
/// and EF Core providers map to. <see cref="ResetAsync"/> truncates between facts.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_dapper_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
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
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE widgets";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS widgets (
                id                uuid          NOT NULL PRIMARY KEY,
                tenant_id         varchar(100)  NULL,
                name              varchar(200)  NOT NULL,
                quantity          int           NOT NULL,
                created_at        timestamptz   NOT NULL,
                created_by        varchar(100)  NULL,
                last_modified_at  timestamptz   NULL,
                last_modified_by  varchar(100)  NULL,
                is_deleted        boolean       NOT NULL DEFAULT false,
                deleted_at        timestamptz   NULL,
                deleted_by        varchar(100)  NULL,
                restored_at       timestamptz   NULL,
                restored_by       varchar(100)  NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }
}
