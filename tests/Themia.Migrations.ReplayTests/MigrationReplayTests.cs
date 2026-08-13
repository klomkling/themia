using System.Data.Common;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Migrations.ReplayTests;

/// <summary>
/// Every Themia migration assembly, applied twice, against a real engine.
/// </summary>
/// <remarks>
/// This is the test the version-ledger change (coord #0078) cannot ship without. Themia migrations moved
/// off FluentMigrator's shared <c>VersionInfo</c> onto a per-assembly ledger, which means that on every
/// EXISTING database each Themia migration is replayed exactly once — the new ledger starts empty and
/// nothing in it says the migration ran. A migration that is not replay-safe turns that upgrade into a
/// failed deploy, and a partially-applied one into something worse.
/// <para>
/// So "run it twice and it must not throw" is not a nicety here; it is the upgrade path, executed.
/// </para>
/// <para>
/// The assembly list is deliberately exhaustive rather than a sample. A migrating package missing from
/// it is a package whose upgrade nobody tested — which is exactly how the collision this all came from
/// went unnoticed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public abstract class MigrationReplayTestsBase
{
    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    protected abstract DbConnection CreateConnection();

    /// <summary>
    /// Every assembly that ships migrations and supports both PostgreSQL and SQL Server. MySQL-only and
    /// MySQL-capable coverage stays in each module's own suite; what is engine-independent here is the
    /// ledger, and what is engine-sensitive is the guards — both are exercised by the pair below.
    /// </summary>
    public static readonly string[] AssemblyNames =
    [
        "Themia.Exceptional",
        "Themia.Challenges",
        "Themia.Scheduling",
        "Themia.AspNetCore.DataProtection",
        "Themia.Modules.Identity",
        "Themia.Modules.Notifications",
        "Themia.Modules.Storage",
        "Themia.Modules.Pdf",
        "Themia.Modules.Messaging",
        "Themia.Modules.Export",
    ];

    public static TheoryData<string> Assemblies()
    {
        var data = new TheoryData<string>();
        foreach (var name in AssemblyNames)
        {
            data.Add(name);
        }

        return data;
    }

    private static Assembly Load(string name) => Assembly.Load(name);

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Applying_twice_is_safe(string assemblyName)
    {
        var assembly = Load(assemblyName);

        ThemiaMigrations.Run(Engine, ConnectionString, assembly);

        // The replay. On a database that already carries these objects — which is every existing
        // deployment on the day it takes this version — the second pass must adopt rather than recreate.
        ThemiaMigrations.Run(Engine, ConnectionString, assembly);
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public async Task Each_assembly_records_in_its_own_ledger(string assemblyName)
    {
        var assembly = Load(assemblyName);
        ThemiaMigrations.Run(Engine, ConnectionString, assembly);

        var table = new ThemiaVersionTable(assembly).TableName;

        await using var connection = CreateConnection();
        var recorded = await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {Quote(table)}");

        Assert.True(recorded > 0, $"{assemblyName} recorded nothing in {table}");
    }

    [Fact]
    public async Task Nothing_is_written_to_the_shared_VersionInfo()
    {
        // The defect in one assertion. Themia used to record here, in the same table a consumer's own
        // FluentMigrator runner uses, so a duplicate version number made one migration of the pair a
        // silent no-op. ezy-assets lost data_protection_keys to that for fifteen days.
        foreach (var name in AssemblyNames)
        {
            ThemiaMigrations.Run(Engine, ConnectionString, Load(name));
        }

        await using var connection = CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(SharedVersionInfoExistsSql);

        Assert.Equal(0, exists);
    }

    [Fact]
    public async Task A_consumer_version_row_no_longer_skips_a_Themia_migration()
    {
        // ezy-assets' production state, reproduced: a consumer's runner has already recorded a version
        // number that a Themia migration also carries. Under the shared ledger the Themia migration was
        // skipped and its table never appeared. It must now apply regardless of what VersionInfo says.
        await using (var seed = CreateConnection())
        {
            await seed.ExecuteAsync(CreateSharedVersionInfoSql);
            // Quoted through the engine-specific helper: PostgreSQL folds an unquoted VersionInfo to
            // versioninfo, so seeding with quotes and inserting without them misses the table entirely.
            await seed.ExecuteAsync(
                $"INSERT INTO {Quote("VersionInfo")} ({Quote("Version")}, {Quote("AppliedOn")}, {Quote("Description")}) "
                + "VALUES (202607260001, @Now, 'consumer migration')",
                new { Now = DateTime.UtcNow });
        }

        ThemiaMigrations.Run(Engine, ConnectionString, Load("Themia.AspNetCore.DataProtection"));

        await using var connection = CreateConnection();
        var tables = await connection.ExecuteScalarAsync<int>(DataProtectionTableExistsSql);

        Assert.Equal(1, tables);
    }

    protected abstract string Quote(string identifier);

    protected abstract string SharedVersionInfoExistsSql { get; }

    protected abstract string CreateSharedVersionInfoSql { get; }

    protected abstract string DataProtectionTableExistsSql { get; }
}

public sealed class PostgresMigrationReplayTests : MigrationReplayTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.Postgres;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    protected override string SharedVersionInfoExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'VersionInfo'";

    protected override string CreateSharedVersionInfoSql =>
        "CREATE TABLE IF NOT EXISTS \"VersionInfo\" (\"Version\" bigint NOT NULL, "
        + "\"AppliedOn\" timestamp NULL, \"Description\" varchar(1024) NULL)";

    protected override string DataProtectionTableExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'data_protection_keys'";

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

public sealed class SqlServerMigrationReplayTests : MigrationReplayTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.SqlServer;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override string Quote(string identifier) => $"[{identifier}]";

    protected override string SharedVersionInfoExistsSql =>
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'VersionInfo'";

    protected override string CreateSharedVersionInfoSql =>
        "IF OBJECT_ID('VersionInfo') IS NULL CREATE TABLE VersionInfo (Version bigint NOT NULL, "
        + "AppliedOn datetime NULL, Description nvarchar(1024) NULL)";

    protected override string DataProtectionTableExistsSql =>
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'data_protection_keys'";

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
