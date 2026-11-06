using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.EFCore.IntegrationTests;

/// <summary>
/// <c>UserConcurrencyVersionMigration</c> on a database that already ran it: the column is there, and a replay
/// (version row gone, migration pending again) must neither fail nor change the column.
/// </summary>
public abstract class UserConcurrencyMigrationTests
{
    private const long MigrationVersion = 202611060001;

    protected abstract MigrationEngine Engine { get; }
    protected abstract string ConnectionString { get; }
    protected abstract DbConnection Open();

    /// <summary>The column's type, nullability and default as the engine reports them.</summary>
    protected abstract string ColumnShapeSql { get; }

    protected abstract string DeleteVersionRowSql { get; }

    [Fact]
    public async Task Column_IsBigintNotNullWithDefaultZero()
    {
        var shape = await ReadShapeAsync();

        Assert.Equal("bigint|NO", shape.Split('|')[0] + "|" + shape.Split('|')[1]);
        Assert.Contains("0", shape.Split('|')[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_CanBeReplayed_WhenItsVersionRowIsGone()
    {
        var before = await ReadShapeAsync();

        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = DeleteVersionRowSql;
            var removed = command.ExecuteNonQuery();
            Assert.Equal(1, removed);
        }

        ThemiaMigrations.Run(Engine, ConnectionString, typeof(IdentitySchemaMigration).Assembly);

        Assert.Equal(before, await ReadShapeAsync());
    }

    private async Task<string> ReadShapeAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ColumnShapeSql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "identity.users.concurrency_version does not exist");
        var shape = $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}";
        Assert.False(await reader.ReadAsync(), "the column exists more than once");
        return shape;
    }

    protected static string VersionRowSql(string versionColumn) =>
        $"DELETE FROM themia_version_themia_modules_identity WHERE {versionColumn} = {MigrationVersion}";
}

[Trait("Category", "Integration")]
public sealed class PostgresUserConcurrencyMigrationTests(PostgresIdentityFixture fixture)
    : UserConcurrencyMigrationTests, IClassFixture<PostgresIdentityFixture>
{
    protected override MigrationEngine Engine => fixture.Engine;
    protected override string ConnectionString => fixture.ConnectionString;
    protected override DbConnection Open() => new NpgsqlConnection(fixture.ConnectionString);

    protected override string ColumnShapeSql =>
        "SELECT data_type, is_nullable, COALESCE(column_default, '') FROM information_schema.columns "
        + "WHERE table_schema = 'identity' AND table_name = 'users' AND column_name = 'concurrency_version'";

    protected override string DeleteVersionRowSql => VersionRowSql("\"Version\"");
}

[Trait("Category", "Integration")]
public sealed class SqlServerUserConcurrencyMigrationTests(SqlServerIdentityFixture fixture)
    : UserConcurrencyMigrationTests, IClassFixture<SqlServerIdentityFixture>
{
    protected override MigrationEngine Engine => fixture.Engine;
    protected override string ConnectionString => fixture.ConnectionString;
    protected override DbConnection Open() => new SqlConnection(fixture.ConnectionString);

    protected override string ColumnShapeSql =>
        "SELECT c.DATA_TYPE, c.IS_NULLABLE, COALESCE(c.COLUMN_DEFAULT, '') FROM INFORMATION_SCHEMA.COLUMNS c "
        + "WHERE c.TABLE_SCHEMA = 'identity' AND c.TABLE_NAME = 'users' AND c.COLUMN_NAME = 'concurrency_version'";

    protected override string DeleteVersionRowSql => VersionRowSql("[Version]");
}
