using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Audit.Migrations;
using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// One real container per engine, shared across every test class for that engine via xUnit's
/// collection-fixture mechanism (<see cref="PostgresAuditStoreCollection"/> and its MySQL/SQL Server
/// siblings), modelled on
/// <c>Themia.Framework.Data.Sequences.IntegrationTests.SequenceEngineFixtures</c>.
/// <see cref="AuditSchemaMigration"/> runs once, here, in <see cref="InitializeAsync"/> — every test
/// class in this project assumes the schema already exists.
/// <para>
/// Test isolation does not come from a fresh container. Every store test class namespaces its own rows
/// with a per-test-method <see cref="Guid"/> (see <c>AuditStoreTestsBase</c>'s <c>TenantId</c> helper),
/// so distinct test methods never collide on the same rows even though every class in an engine's
/// collection shares one table on one server, and no test in this project asserts a total row count.
/// </para>
/// <para>
/// The per-engine migration test class (<c>PostgresAuditSchemaMigrationTests</c> and its siblings) is the
/// one class per engine that mutates the schema/ledger itself rather than just seeding namespaced rows —
/// it re-runs <see cref="ThemiaMigrations.Run"/> directly and deletes rows from the migration's own
/// version-ledger table to prove replay-safety. It still joins the same collection as the store tests
/// rather than getting a dedicated container, deliberately, for the same three reasons
/// <c>SequencesSchemaMigrationTests</c> gives: (1) <see cref="AuditSchemaMigration.Up"/> guards its
/// <c>CREATE TABLE</c> with <c>Schema.Table(...).Exists()</c>, so a rerun against an already-migrated
/// table is a no-op that only rewrites the ledger row, never <c>themia_audit_events</c>' data; (2)
/// <see cref="ThemiaMigrations.Run"/> itself takes an exclusive advisory lock for the duration of the run,
/// so no concurrent migration attempt can interleave with it; and (3) xUnit never runs two test classes
/// from the same collection concurrently, so nothing else is querying this connection while the ledger row
/// is briefly deleted. All three conditions hold for Audit exactly as they do for Sequences — do not
/// re-split this onto a dedicated container; sharing it is what keeps this project at exactly three
/// containers total, not six.
/// </para>
/// <para>
/// The migration test's own schema assertions check column presence via <c>information_schema</c>, never
/// row count or table emptiness — an emptiness assertion is what forced an earlier, incorrect design onto
/// six containers (three dedicated to migration tests, three shared by everything else), because it broke
/// the moment a store test in the same collection had already inserted a row.
/// </para>
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

/// <summary>PostgreSQL fixture: one <c>postgres:16-alpine</c> container shared by every PostgreSQL test
/// class in this project.</summary>
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

/// <summary>MySQL fixture: one <c>mysql:8.4</c> container shared by every MySQL test class in this
/// project.</summary>
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

/// <summary>SQL Server fixture: one <c>mssql/server:2022-CU14-ubuntu-22.04</c> container shared by every
/// SQL Server test class in this project.</summary>
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

/// <summary>xUnit collection tying every PostgreSQL test class in this project — store and migration
/// alike — to one shared <see cref="PostgresAuditStoreFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresAuditStoreCollection : ICollectionFixture<PostgresAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Audit";
}

/// <summary>xUnit collection tying every MySQL test class in this project — store and migration alike —
/// to one shared <see cref="MySqlAuditStoreFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlAuditStoreCollection : ICollectionFixture<MySqlAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Audit";
}

/// <summary>xUnit collection tying every SQL Server test class in this project — store and migration
/// alike — to one shared <see cref="SqlServerAuditStoreFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerAuditStoreCollection : ICollectionFixture<SqlServerAuditStoreFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Audit";
}
