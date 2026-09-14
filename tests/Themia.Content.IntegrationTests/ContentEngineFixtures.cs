using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Themia.Content.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Xunit;

namespace Themia.Content.IntegrationTests;

/// <summary>
/// One real container per engine, shared by every test class for that engine through an xUnit collection fixture.
/// Registration goes through the same <c>AddThemiaContent&lt;Engine&gt;</c> path an adopter uses, which runs the
/// migration as a side effect. Tests in one collection run sequentially and use their own slugs.
/// </summary>
public abstract class ContentEngineFixture : IAsyncLifetime
{
    private ServiceProvider provider = null!;

    /// <summary>The container's connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The engine's dialect, for raw SQL no public API exposes.</summary>
    public IContentPageDialect Dialect { get; private set; } = null!;

    /// <summary>The clock every service in this fixture uses. Starts at a whole second so every engine stores it exactly.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero));

    /// <summary>The engine's migration adapter, for re-running the migration.</summary>
    public abstract IMigrationEngineAdapter MigrationAdapter { get; }

    /// <summary>Starts the container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the container.</summary>
    protected abstract Task StopContainerAsync();

    /// <summary>Calls the engine package's registration method.</summary>
    protected abstract void RegisterEngine(IServiceCollection services, string connectionString);

    /// <summary>Whether a table named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> TableExistsAsync(string name);

    /// <summary>Whether an index named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> IndexExistsAsync(string name);

    /// <summary>Builds the service collection. Task 5 adds the core registration here.</summary>
    protected virtual void ConfigureServices(IServiceCollection services, string connectionString)
    {
        services.AddSingleton<TimeProvider>(Time);
        RegisterEngine(services, connectionString);
    }

    /// <summary>Resolves what tests use. Task 5 adds the service here.</summary>
    protected virtual void Resolve(IServiceProvider services) =>
        Dialect = services.GetRequiredService<IContentPageDialect>();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        var services = new ServiceCollection();
        ConfigureServices(services, ConnectionString);
        provider = services.BuildServiceProvider();
        Resolve(provider);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await StopContainerAsync();
    }

    /// <summary>Runs a scalar query on a fresh connection.</summary>
    protected async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}

/// <summary>PostgreSQL: one <c>postgres:16-alpine</c> container for every PostgreSQL test class.</summary>
public sealed class PostgresContentFixture : ContentEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => PostgresMigrationEngine.Adapter;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterEngine(IServiceCollection services, string connectionString) =>
        services.AddThemiaContentPostgres(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @Name);", new { Name = name });
}

/// <summary>Ties every PostgreSQL test class to one <see cref="PostgresContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresContentCollection : ICollectionFixture<PostgresContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "Postgres Content";
}
