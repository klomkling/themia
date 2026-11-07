using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.SecondFactor.PostgreSql;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

/// <summary>One real PostgreSQL 15 (the minimum the schema supports), shared by every test class, wired through the same DI path an adopter uses.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:15-alpine").Build();

    public string ConnectionString => container.GetConnectionString();

    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Services = new ServiceCollection().AddThemiaSecondFactorPostgres(ConnectionString).BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await container.DisposeAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "second-factor-postgres";
}
