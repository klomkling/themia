using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Themia.Content.DependencyInjection;
using Themia.Content.Internal;
using Themia.Content.MySql;
using Themia.Content.PostgreSql;
using Themia.Content.SqlServer;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Migrations.SqlServer;
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

    /// <summary>The live service, resolved from DI exactly as an adopter resolves it.</summary>
    public IContentPageService Service { get; private set; } = null!;

    /// <summary>The live importer, resolved from DI exactly as an adopter resolves it.</summary>
    public IContentPageImporter Importer { get; private set; } = null!;

    /// <summary>The resolved options: languages th and en, fallback th.</summary>
    public ContentOptions Options { get; private set; } = null!;

    /// <summary>A service over <paramref name="dialect"/> with this fixture's options and clock — for tests that wrap
    /// the real dialect.</summary>
    internal ContentPageService NewService(IContentPageDialect dialect) =>
        new(dialect, Options, Time, NullLogger<ContentPageService>.Instance);

    /// <summary>Builds the service collection through the adopter's registration path.</summary>
    protected virtual void ConfigureServices(IServiceCollection services, string connectionString)
    {
        services.AddSingleton<TimeProvider>(Time);
        services.AddThemiaContent(options =>
        {
            options.Languages.Add("th");
            options.Languages.Add("en");
            options.FallbackLanguage = "th";
        });
        RegisterEngine(services, connectionString);
    }

    /// <summary>Resolves what tests use.</summary>
    protected virtual void Resolve(IServiceProvider services)
    {
        Dialect = services.GetRequiredService<IContentPageDialect>();
        Service = services.GetRequiredService<IContentPageService>();
        Importer = services.GetRequiredService<IContentPageImporter>();
        Options = services.GetRequiredService<ContentOptions>();
    }

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

    /// <summary>Deletes every page and revision. Import tests need empty tables; tests in one collection run sequentially,
    /// and no other test reads rows it did not create in the same test.</summary>
    public async Task ClearContentAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM content_page_revisions;");
        await connection.ExecuteAsync("DELETE FROM content_pages;");
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

/// <summary>MySQL: one <c>mysql:8.4</c> container for every MySQL test class.</summary>
public sealed class MySqlContentFixture : ContentEngineFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => MySqlMigrationEngine.Adapter;

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
        services.AddThemiaContentMySql(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @Name);",
            new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = @Name);",
            new { Name = name });
}

/// <summary>Ties every MySQL test class to one <see cref="MySqlContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlContentCollection : ICollectionFixture<MySqlContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "MySql Content";
}

/// <summary>SQL Server: one <c>mssql/server:2022-CU14-ubuntu-22.04</c> container for every SQL Server test class.</summary>
public sealed class SqlServerContentFixture : ContentEngineFixture
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => SqlServerMigrationEngine.Adapter;

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
        services.AddThemiaContentSqlServer(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE name = @Name) THEN 1 ELSE 0 END AS bit);",
            new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = @Name) THEN 1 ELSE 0 END AS bit);",
            new { Name = name });
}

/// <summary>Ties every SQL Server test class to one <see cref="SqlServerContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerContentCollection : ICollectionFixture<SqlServerContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "SqlServer Content";
}
