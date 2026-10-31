using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Themia.Conversations.DependencyInjection;
using Themia.Conversations.MySql;
using Themia.Conversations.PostgreSql;
using Themia.Conversations.SqlServer;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Migrations.SqlServer;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>
/// One real container per engine, shared by every test class for that engine through an xUnit collection fixture. Registration goes through the same
/// <c>AddThemiaConversations&lt;Engine&gt;</c> path an adopter uses, which runs the migration as a side effect. Tests use their own random tenant ids.
/// </summary>
public abstract class ConversationEngineFixture : IAsyncLifetime
{
    private ServiceProvider provider = null!;

    /// <summary>The container's connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The engine's dialect (its connection is what the tests use for raw SQL).</summary>
    public IConversationDialect Dialect { get; private set; } = null!;

    /// <summary>The container built by <c>AddThemiaConversations</c> plus the engine registration, the way an adopter builds it.</summary>
    public IServiceProvider Services => provider;

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

    /// <summary>Whether an index or unique constraint named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> IndexExistsAsync(string name);

    /// <summary>Whether a foreign key named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> ForeignKeyExistsAsync(string name);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaConversations();
        RegisterEngine(services, ConnectionString);
        provider = services.BuildServiceProvider();
        Dialect = provider.GetRequiredService<IConversationDialect>();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await StopContainerAsync();
    }

    /// <summary>Runs a statement on a fresh connection, so a failed one cannot poison the next.</summary>
    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }

    /// <summary>Runs a scalar query on a fresh connection.</summary>
    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}

/// <summary>PostgreSQL: one <c>postgres:16-alpine</c> container for every PostgreSQL test class.</summary>
public sealed class PostgresConversationFixture : ConversationEngineFixture
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
        services.AddThemiaConversationsPostgres(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> ForeignKeyExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE contype = 'f' AND conname = @Name);", new { Name = name });
}

/// <summary>Ties every PostgreSQL test class to one <see cref="PostgresConversationFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresConversationCollection : ICollectionFixture<PostgresConversationFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "Postgres Conversations";
}

/// <summary>MySQL: one <c>mysql:8.4</c> container for every MySQL test class.</summary>
public sealed class MySqlConversationFixture : ConversationEngineFixture
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
        services.AddThemiaConversationsMySql(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> ForeignKeyExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND constraint_type = 'FOREIGN KEY' AND constraint_name = @Name);",
            new { Name = name });
}

/// <summary>Ties every MySQL test class to one <see cref="MySqlConversationFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlConversationCollection : ICollectionFixture<MySqlConversationFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "MySql Conversations";
}

/// <summary>SQL Server: one container for every SQL Server test class.</summary>
public sealed class SqlServerConversationFixture : ConversationEngineFixture
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

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
        services.AddThemiaConversationsSqlServer(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE name = @Name) THEN 1 ELSE 0 END;", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = @Name) THEN 1 ELSE 0 END;", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> ForeignKeyExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = @Name) THEN 1 ELSE 0 END;", new { Name = name });
}

/// <summary>Ties every SQL Server test class to one <see cref="SqlServerConversationFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerConversationCollection : ICollectionFixture<SqlServerConversationFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "SqlServer Conversations";
}
