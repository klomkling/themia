using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests.Fixtures;

/// <summary>Starts a PostgreSQL Testcontainer, runs Identity migrations, and exposes a reset helper.</summary>
public sealed class PostgresAuthFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_auth_tests")
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

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "TRUNCATE identity.refresh_tokens, identity.user_tokens, identity.user_claims, identity.role_claims, " +
            "identity.user_roles, identity.users, identity.roles RESTART IDENTITY CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
