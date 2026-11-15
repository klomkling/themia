using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
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

    /// <summary>Creates an empty database next to the shared one and returns its connection string, for tests that run migrations themselves.</summary>
    public abstract Task<string> CreateFreshDatabaseAsync();

    /// <summary>Quotes an identifier the way this engine's migration runner created it.</summary>
    public abstract string Quote(string identifier);

    /// <summary>The statements that undo what a migration run that died halfway would not have done: the upload table's indexes and, where the engine pins them, its key collations.</summary>
    public abstract IReadOnlyList<string> PartialStateSql();

    /// <summary>Opens nothing yet: a connection of this engine to the given database.</summary>
    public abstract DbConnection Connect(string connectionString);

    /// <summary>Whether an index of the given <c>@Name</c> exists (a bool or 0/1 scalar).</summary>
    public abstract string IndexExistsSql { get; }

    /// <summary>The collation of column <c>@c</c> of table <c>@t</c>, or null where the engine has no per-column collation to pin.</summary>
    public abstract string? CollationSql { get; }

    /// <summary>Runs a statement against another database of this container.</summary>
    public static async Task<T> ScalarOnAsync<T>(DbConnection connection, string sql, object? parameters = null)
    {
        await using var _ = connection;
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    /// <summary>Runs a statement against another database of this container.</summary>
    public static async Task ExecuteOnAsync(DbConnection connection, string sql)
    {
        await using var _ = connection;
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    /// <summary>The statement that takes a row lock on one conversation and fails at once, instead of waiting, when another transaction holds it.</summary>
    protected abstract string TryLockConversationSql { get; }

    /// <summary>Whether <paramref name="exception"/> is this engine's "lock not available" answer to <see cref="TryLockConversationSql"/>.</summary>
    protected abstract bool IsLockNotAvailable(DbException exception);

    /// <summary>
    /// Whether another transaction holds the row lock of conversation <paramref name="conversationId"/>, probed from a separate connection with no wait.
    /// Any other failure propagates, so a broken probe cannot read as "locked".
    /// </summary>
    public async Task<bool> IsConversationRowLockedAsync(Guid conversationId)
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await connection.ExecuteScalarAsync<int?>(TryLockConversationSql, new { Id = conversationId }, transaction);
            return false;
        }
        catch (DbException ex) when (IsLockNotAvailable(ex))
        {
            return true;
        }
    }

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
    protected override string TryLockConversationSql => "SELECT 1 FROM conversations WHERE id = @Id FOR UPDATE NOWAIT;";

    /// <inheritdoc />
    protected override bool IsLockNotAvailable(DbException exception) => exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable };

    /// <inheritdoc />
    public override async Task<string> CreateFreshDatabaseAsync()
    {
        var name = "fresh_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE {name};");
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    /// <inheritdoc />
    public override string Quote(string identifier) => "\"" + identifier + "\"";

    /// <inheritdoc />
    public override IReadOnlyList<string> PartialStateSql() =>
        ["DROP INDEX ix_conversation_attachment_uploads_created;", "DROP INDEX ix_conversation_attachment_uploads_conversation;"];


    /// <inheritdoc />
    public override DbConnection Connect(string connectionString) => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public override string IndexExistsSql => "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @Name);";

    /// <inheritdoc />
    public override string? CollationSql => null;

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
    protected override string TryLockConversationSql => "SELECT 1 FROM conversations WHERE id = @Id FOR UPDATE NOWAIT;";

    /// <inheritdoc />
    protected override bool IsLockNotAvailable(DbException exception) => exception is MySqlException { Number: 3572 }; // ER_LOCK_NOWAIT

    /// <inheritdoc />
    public override async Task<string> CreateFreshDatabaseAsync()
    {
        var name = "fresh_" + Guid.NewGuid().ToString("N");
        var root = new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root", Pooling = false };
        await ExecuteOnAsync(new MySqlConnection(root.ConnectionString), $"CREATE DATABASE {name};");
        return new MySqlConnectionStringBuilder(ConnectionString) { Database = name, UserID = "root", Pooling = false }.ConnectionString;
    }

    /// <inheritdoc />
    public override string Quote(string identifier) => "`" + identifier + "`";

    /// <inheritdoc />
    public override IReadOnlyList<string> PartialStateSql() =>
    [
        "DROP INDEX ix_conversation_attachment_uploads_created ON conversation_attachment_uploads;",
        "DROP INDEX ix_conversation_attachment_uploads_conversation ON conversation_attachment_uploads;",
        "ALTER TABLE conversation_attachment_uploads MODIFY COLUMN `tenant_id` VARCHAR(64) COLLATE utf8mb4_general_ci NOT NULL;",
        "ALTER TABLE conversation_attachment_uploads MODIFY COLUMN `storage_key` VARCHAR(300) COLLATE utf8mb4_general_ci NOT NULL;",
    ];


    /// <inheritdoc />
    public override DbConnection Connect(string connectionString) => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public override string IndexExistsSql => "SELECT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = @Name);";

    /// <inheritdoc />
    public override string? CollationSql =>
        "SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c;";

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
    protected override string TryLockConversationSql => "SET LOCK_TIMEOUT 0; SELECT 1 FROM conversations WITH (UPDLOCK, ROWLOCK) WHERE id = @Id;";

    /// <inheritdoc />
    protected override bool IsLockNotAvailable(DbException exception) => exception is SqlException { Number: 1222 }; // lock request time out

    /// <inheritdoc />
    public override async Task<string> CreateFreshDatabaseAsync()
    {
        var name = "fresh_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE {name};");
        return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name, Pooling = false }.ConnectionString;
    }

    /// <inheritdoc />
    public override string Quote(string identifier) => "[" + identifier + "]";

    /// <inheritdoc />
    public override IReadOnlyList<string> PartialStateSql() =>
    [
        "DROP INDEX ix_conversation_attachment_uploads_created ON conversation_attachment_uploads;",
        "DROP INDEX ix_conversation_attachment_uploads_conversation ON conversation_attachment_uploads;",
        "ALTER TABLE conversation_attachment_uploads ALTER COLUMN [tenant_id] NVARCHAR(64) COLLATE Latin1_General_CI_AS NOT NULL;",
        "ALTER TABLE conversation_attachment_uploads ALTER COLUMN [storage_key] NVARCHAR(300) COLLATE Latin1_General_CI_AS NOT NULL;",
    ];


    /// <inheritdoc />
    public override DbConnection Connect(string connectionString) => new SqlConnection(connectionString);

    /// <inheritdoc />
    public override string IndexExistsSql => "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = @Name) THEN 1 ELSE 0 END;";

    /// <inheritdoc />
    public override string? CollationSql => "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND name = @c;";

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
