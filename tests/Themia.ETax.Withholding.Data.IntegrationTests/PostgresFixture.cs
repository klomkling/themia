using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.ETax.Withholding.Data.IntegrationTests;

/// <summary>One PostgreSQL container for the whole project; every test gets its own empty database, so tests never see each other's tables, triggers or search_path.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The data lives in memory: these databases are thrown away, and it keeps the container fast.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its connection string (optionally with a search_path).</summary>
    public async Task<string> CreateDatabaseAsync(string? searchPath = null)
    {
        var name = "etaxw_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false };
        if (searchPath is not null)
            builder.SearchPath = searchPath;
        return builder.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "etax-withholding-data-postgres";
}
