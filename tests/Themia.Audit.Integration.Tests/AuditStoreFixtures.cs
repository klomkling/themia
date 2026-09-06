using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Audit.Migrations;
using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Integration.Tests;

/// <summary>
/// One real container per engine, dedicated to store/behaviour tests — never shared with
/// <c>Themia.Audit.Migration.Tests</c>'s own per-engine containers (see the container-discipline note
/// on <c>AuditMigrationFixture</c>). <see cref="AuditSchemaMigration"/> runs once, here, in
/// <see cref="InitializeAsync"/>; every test class in this project assumes the schema already exists and
/// scopes its own rows by a per-test <c>TenantId</c> so concurrent-looking classes sharing one table
/// never see each other's rows.
/// </summary>
public abstract class AuditStoreFixture : IAsyncLifetime
{
    /// <summary>The engine this fixture's container runs.</summary>
    public abstract MigrationEngine Engine { get; }

    /// <summary>The dialect for this engine.</summary>
    public abstract IAuditDialect Dialect { get; }

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has completed.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>The store under test, backed by <see cref="Dialect"/>.</summary>
    public IAuditStore Store { get; private set; } = null!;

    /// <summary>Starts the engine's container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the engine's container.</summary>
    protected abstract Task StopContainerAsync();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(AuditSchemaMigration).Assembly);
        Store = new AuditStoreEngine(Dialect);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await StopContainerAsync();
}

/// <summary>PostgreSQL fixture, dedicated to store/behaviour tests.</summary>
public sealed class PostgresAuditStoreFixture : AuditStoreFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override MigrationEngine Engine => MigrationEngine.Postgres;

    /// <inheritdoc />
    public override IAuditDialect Dialect { get; } = new PostgreSql.PostgresAuditDialect();

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>MySQL fixture, dedicated to store/behaviour tests.</summary>
public sealed class MySqlAuditStoreFixture : AuditStoreFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    public override MigrationEngine Engine => MigrationEngine.MySql;

    /// <inheritdoc />
    public override IAuditDialect Dialect { get; } = new MySql.MySqlAuditDialect();

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>SQL Server fixture, dedicated to store/behaviour tests.</summary>
public sealed class SqlServerAuditStoreFixture : AuditStoreFixture
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    public override MigrationEngine Engine => MigrationEngine.SqlServer;

    /// <inheritdoc />
    public override IAuditDialect Dialect { get; } = new SqlServer.SqlServerAuditDialect();

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>xUnit collection tying the PostgreSQL store test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresAuditStoreCollection : ICollectionFixture<PostgresAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Audit Store";
}

/// <summary>xUnit collection tying the MySQL store test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlAuditStoreCollection : ICollectionFixture<MySqlAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Audit Store";
}

/// <summary>xUnit collection tying the SQL Server store test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerAuditStoreCollection : ICollectionFixture<SqlServerAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Audit Store";
}
