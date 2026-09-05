using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// One real container per engine, shared across every test class for that engine via xUnit's
/// collection-fixture mechanism (<see cref="PostgresSequenceCollection"/> and its MySQL/SQL Server
/// siblings), so the test classes in this project reuse a single running Postgres/MySQL/SQL Server
/// instance instead of each starting its own. <see cref="SequencesSchemaMigration"/> runs once, here, in
/// <see cref="InitializeAsync"/> — every test class assumes the schema already exists.
/// <para>
/// Test isolation no longer comes from a fresh container. Every test class namespaces its own sequence
/// keys with a per-instance <see cref="Guid"/> (each test class's own <c>keyNamespace</c>/<c>Key</c>
/// helper), so distinct test methods never collide on the same row even though every class in an engine's
/// collection shares one table on one server.
/// </para>
/// <para>
/// <see cref="SequencesSchemaMigrationTests"/> is the one class here that mutates the schema/ledger
/// itself rather than just seeding namespaced rows — it re-runs <see cref="ThemiaMigrations.Run"/>
/// directly and deletes rows from the migration's own version-ledger table to prove replay-safety. It
/// still joins <see cref="PostgresSequenceCollection"/> rather than getting a dedicated container,
/// deliberately: (1) <c>SequencesSchemaMigration.Up()</c> guards its <c>CREATE TABLE</c> with
/// <c>Schema.Table(...).Exists()</c>, so a rerun against an already-migrated table is a no-op that only
/// rewrites the ledger row, never <c>themia_sequences</c>' data; (2) <c>ThemiaMigrations.Run</c> itself
/// takes an exclusive advisory lock for the duration of the run (see <c>MigrationLock</c>), so no
/// concurrent migration attempt can interleave with it; and (3) xUnit never runs two test classes from the
/// same collection concurrently, so nothing else is querying this connection while the ledger row is
/// briefly deleted. Sharing it is what gets this project to exactly three containers total.
/// </para>
/// </summary>
public abstract class SequenceEngineFixture : IAsyncLifetime
{
    /// <summary>The engine this fixture's container runs, and the value every test class in its
    /// collection configures <see cref="Framework.Data.Sequences.SequenceOptions.Engine"/> with.</summary>
    public abstract SequenceEngine Engine { get; }

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has
    /// completed.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Starts the engine's container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the engine's container.</summary>
    protected abstract Task StopContainerAsync();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        ThemiaMigrations.Run(ToMigrationEngine(Engine), ConnectionString, typeof(SequencesSchemaMigration).Assembly);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await StopContainerAsync();

    /// <summary>Maps to the neutral migration runner's own engine selector — kept as a mapping rather than
    /// one shared enum because <c>Themia.Data.Migrations</c> cannot reference the framework's provider
    /// names (see <see cref="SequenceEngine"/>'s own remarks).</summary>
    private static MigrationEngine ToMigrationEngine(SequenceEngine engine) => engine switch
    {
        SequenceEngine.Postgres => MigrationEngine.Postgres,
        SequenceEngine.MySql => MigrationEngine.MySql,
        SequenceEngine.SqlServer => MigrationEngine.SqlServer,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown sequence engine."),
    };
}

/// <summary>PostgreSQL fixture: one <c>postgres:16-alpine</c> container shared by every PostgreSQL test
/// class in this project.</summary>
public sealed class PostgresSequenceFixture : SequenceEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override SequenceEngine Engine => SequenceEngine.Postgres;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>
/// A PostgreSQL container used by <see cref="SequencesSchemaMigrationTests"/> ALONE.
/// </summary>
/// <remarks>
/// Every other test class is safe to share a container because each test namespaces its own sequence
/// keys. The migration tests are not: they mutate the schema and the per-assembly version ledger
/// themselves — one deletes every ledger row and re-runs the migration to prove replay-safety — and no
/// amount of key namespacing isolates that from a class reading the same tables.
/// <para>
/// Sharing appeared to work: <c>Up()</c> is idempotent behind its schema-exists guard,
/// <c>ThemiaMigrations.Run</c> holds an exclusive advisory lock, and xUnit serialises classes within one
/// collection, so three consecutive runs passed. That is an argument, not a guarantee — it rests on
/// three separate mechanisms staying true, and shared-state failures are the ones a green run does not
/// surface. One extra container is cheaper than the day spent diagnosing an ordering-dependent red in
/// CI, so these tests get their own.
/// </para>
/// </remarks>
public sealed class MigrationSequenceFixture : SequenceEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override SequenceEngine Engine => SequenceEngine.Postgres;

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
public sealed class MySqlSequenceFixture : SequenceEngineFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    public override SequenceEngine Engine => SequenceEngine.MySql;

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
public sealed class SqlServerSequenceFixture : SequenceEngineFixture
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    public override SequenceEngine Engine => SequenceEngine.SqlServer;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>xUnit collection tying every PostgreSQL test class in this project to one shared
/// <see cref="PostgresSequenceFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresSequenceCollection : ICollectionFixture<PostgresSequenceFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Sequences";
}

/// <summary>xUnit collection tying every MySQL test class in this project to one shared
/// <see cref="MySqlSequenceFixture"/> container instance.</summary>
/// <summary>Collection for the migration tests, isolated on their own container.</summary>
[CollectionDefinition(Name)]
public sealed class MigrationSequenceCollection : ICollectionFixture<MigrationSequenceFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "themia-sequences-migration";
}

[CollectionDefinition(Name)]
public sealed class MySqlSequenceCollection : ICollectionFixture<MySqlSequenceFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Sequences";
}

/// <summary>xUnit collection tying every SQL Server test class in this project to one shared
/// <see cref="SqlServerSequenceFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerSequenceCollection : ICollectionFixture<SqlServerSequenceFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Sequences";
}
