using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>
/// Spins up a real SQL Server container and creates the shared <c>widgets</c> table the Dapper provider maps to.
/// Tables live in the default <c>master</c> database (the mssql image creates no custom database).
/// <see cref="ResetAsync"/> truncates between facts.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    /// <summary>The connection string to the running container (master database).</summary>
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
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE widgets";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'widgets', N'U') IS NULL
            CREATE TABLE widgets (
                id                UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
                tenant_id         NVARCHAR(100)     NULL,
                name              NVARCHAR(200)     NOT NULL,
                quantity          INT               NOT NULL,
                created_at        DATETIME2(7)      NOT NULL,
                created_by        NVARCHAR(100)     NULL,
                last_modified_at  DATETIME2(7)      NULL,
                last_modified_by  NVARCHAR(100)     NULL,
                is_deleted        BIT               NOT NULL DEFAULT 0,
                deleted_at        DATETIME2(7)      NULL,
                deleted_by        NVARCHAR(100)     NULL,
                restored_at       DATETIME2(7)      NULL,
                restored_by       NVARCHAR(100)     NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }
}
