using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Migration.Tests;

/// <summary>
/// One real container per engine, dedicated to migration tests only — never shared with
/// <c>Themia.Audit.Integration.Tests</c>' store/behaviour fixtures. A migration test replays
/// <c>AuditSchemaMigration</c> and deletes its own version-ledger row to prove replay-safety; running
/// that against a container a behaviour test has already written rows into would make one suite's
/// mutation of shared state (the ledger) leak into the other. See the task-2 container discipline note:
/// migration tests get their own container per engine, store tests share a second — six total, not one
/// shared set of three.
/// </summary>
public abstract class AuditMigrationFixture : IAsyncLifetime
{
    /// <summary>The engine this fixture's container runs.</summary>
    public abstract MigrationEngine Engine { get; }

    /// <summary>The dialect for this engine, used to open ad-hoc verification connections.</summary>
    public abstract IAuditDialect Dialect { get; }

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has completed.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Starts the engine's container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the engine's container.</summary>
    protected abstract Task StopContainerAsync();

    /// <inheritdoc />
    public async Task InitializeAsync() => ConnectionString = await StartContainerAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await StopContainerAsync();
}

/// <summary>PostgreSQL fixture, dedicated to migration tests.</summary>
public sealed class PostgresAuditMigrationFixture : AuditMigrationFixture
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

/// <summary>MySQL fixture, dedicated to migration tests.</summary>
public sealed class MySqlAuditMigrationFixture : AuditMigrationFixture
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

/// <summary>SQL Server fixture, dedicated to migration tests.</summary>
public sealed class SqlServerAuditMigrationFixture : AuditMigrationFixture
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

/// <summary>xUnit collection tying the PostgreSQL migration test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresAuditMigrationCollection : ICollectionFixture<PostgresAuditMigrationFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Audit Migration";
}

/// <summary>xUnit collection tying the MySQL migration test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlAuditMigrationCollection : ICollectionFixture<MySqlAuditMigrationFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Audit Migration";
}

/// <summary>xUnit collection tying the SQL Server migration test class to its dedicated fixture.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerAuditMigrationCollection : ICollectionFixture<SqlServerAuditMigrationFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Audit Migration";
}
