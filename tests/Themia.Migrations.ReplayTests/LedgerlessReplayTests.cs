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
/// The 0.15 → 0.16 upgrade, executed: every Themia object present, no per-assembly ledger.
/// </summary>
/// <remarks>
/// <see cref="MigrationReplayTestsBase.Applying_twice_is_safe"/> is named for this and does not test it.
/// Its first pass records the version in <c>themia_version_&lt;assembly&gt;</c>, so FluentMigrator skips the
/// migration on the second pass and <c>Up()</c> is never re-entered. It therefore exercises the runner's
/// idempotence, not the migration body's — which is the half that meets an existing database.
/// <para>
/// Dropping the ledger between the two passes reproduces the upgrade state faithfully without installing
/// the old package, and is the only thing that forces <c>Up()</c> to run against objects that already
/// exist. The shape is ezy-assets' from coord #0085.
/// </para>
/// <para>
/// Run on BOTH engines. The guards these tests exist for are written once but execute per engine, and
/// <see cref="MigrationReplayTestsBase.Applying_twice_is_safe"/> cannot reach them on either — so without
/// a SQL Server leg here, every guard in every schema migration would be exercised on PostgreSQL only
/// while shipping to consumers on both.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public abstract class LedgerlessReplayTestsBase
{
    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    protected abstract DbConnection CreateConnection();

    protected abstract string TableExistsSql { get; }

    public static TheoryData<string> Assemblies()
    {
        var data = new TheoryData<string>();
        foreach (var name in MigrationReplayTestsBase.AssemblyNames)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public async Task Replays_onto_its_own_objects_when_the_ledger_is_gone(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        ThemiaMigrations.Run(Engine, ConnectionString, assembly);

        var ledger = new ThemiaVersionTable(assembly).TableName;
        await using (var connection = CreateConnection())
        {
            await connection.ExecuteAsync($"DROP TABLE {Quote(ledger)}");
        }

        // Everything this assembly creates is now present and nothing records that it ran — the state
        // every existing database was in on the day it took 0.16.
        ThemiaMigrations.Run(Engine, ConnectionString, assembly);
    }

    /// <summary>
    /// Why the existing suite cannot see any of that: with the ledger intact the second pass does not
    /// execute the migration at all.
    /// </summary>
    [Fact]
    public async Task A_second_pass_with_the_ledger_intact_never_re_enters_the_migration()
    {
        var assembly = Assembly.Load("Themia.AspNetCore.DataProtection");
        ThemiaMigrations.Run(Engine, ConnectionString, assembly);

        await using (var connection = CreateConnection())
        {
            await connection.ExecuteAsync("DROP TABLE data_protection_keys");
        }

        ThemiaMigrations.Run(Engine, ConnectionString, assembly);

        // A replayed Up() would have recreated it. The ledger says applied, so nothing ran, and the
        // table the assembly exists to manage is still missing — with the run reporting success.
        await using var check = CreateConnection();
        var count = await check.ExecuteScalarAsync<int>(TableExistsSql);
        Assert.Equal(0, count);
    }

    protected abstract string Quote(string identifier);
}

public sealed class PostgresLedgerlessReplayTests : LedgerlessReplayTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.Postgres;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    protected override string TableExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'data_protection_keys'";

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

public sealed class SqlServerLedgerlessReplayTests : LedgerlessReplayTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").WithCleanUp(true).Build();

    protected override MigrationEngine Engine => MigrationEngine.SqlServer;

    protected override string ConnectionString => container.GetConnectionString();

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override string Quote(string identifier) => $"[{identifier}]";

    protected override string TableExistsSql =>
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'data_protection_keys'";

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
