using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.AspNetCore.DataProtection.Migrations;
using Themia.Data.Migrations;

using Xunit;

namespace Themia.AspNetCore.DataProtection.IntegrationTests;

/// <summary>
/// One real container per engine, shared across every test class for that engine via xUnit's
/// collection-fixture mechanism (<see cref="PostgresDataProtectionCollection"/> and its MySQL/SQL Server
/// siblings) — the Postgres collection is joined by both <see cref="DataProtectionKeyStorePostgresTests"/>
/// and <see cref="DataProtectionSchemaProbeTests"/> — instead of each test class/method starting its own.
/// The schema migration runs once, here, in <see cref="InitializeAsync"/>.
/// <para>
/// Test isolation no longer comes from a fresh container: <see cref="DataProtectionKeyStoreTestsBase"/>
/// deletes every row from <c>data_protection_keys</c> before each of its tests, so it still starts from an
/// empty table even though every test class in an engine's collection now shares one table on one server.
/// </para>
/// </summary>
public abstract class DataProtectionEngineFixture : IAsyncLifetime
{
    /// <summary>The engine this fixture's container runs.</summary>
    protected abstract MigrationEngine Engine { get; }

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
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(DataProtectionKeysMigration).Assembly);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await StopContainerAsync();
}

/// <summary>PostgreSQL fixture: one <c>postgres:16-alpine</c> container shared by every PostgreSQL test
/// class in this assembly.</summary>
public sealed class PostgresDataProtectionFixture : DataProtectionEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    protected override MigrationEngine Engine => MigrationEngine.Postgres;

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
/// assembly.</summary>
public sealed class MySqlDataProtectionFixture : DataProtectionEngineFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    protected override MigrationEngine Engine => MigrationEngine.MySql;

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
/// SQL Server test class in this assembly.</summary>
public sealed class SqlServerDataProtectionFixture : DataProtectionEngineFixture
{
    // MsSqlBuilder 4.12.0: parameterless ctor is [Obsolete] as error — must pass image explicitly.
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    protected override MigrationEngine Engine => MigrationEngine.SqlServer;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();
}

/// <summary>xUnit collection tying every PostgreSQL test class in this assembly to one shared
/// <see cref="PostgresDataProtectionFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresDataProtectionCollection : ICollectionFixture<PostgresDataProtectionFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres DataProtection";
}

/// <summary>xUnit collection tying every MySQL test class in this assembly to one shared
/// <see cref="MySqlDataProtectionFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlDataProtectionCollection : ICollectionFixture<MySqlDataProtectionFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql DataProtection";
}

/// <summary>xUnit collection tying every SQL Server test class in this assembly to one shared
/// <see cref="SqlServerDataProtectionFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerDataProtectionCollection : ICollectionFixture<SqlServerDataProtectionFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer DataProtection";
}
