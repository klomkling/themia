using Themia.Exceptional.Alerts;
using Themia.Data.Migrations;
using Themia.Exceptional.Conformance;
using Themia.Exceptional.Migrations;

using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

[Collection(MySqlExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class MySqlExceptionAlertStoreTests(MySqlExceptionalFixture fixture) : ExceptionAlertStoreConformanceTests
{
    protected MySqlExceptionalFixture Fixture => fixture;

    protected override IExceptionAlertSqlDialect AlertDialect => new MySqlExceptionalDialect(fixture.ConnectionString);

    protected override IExceptionStore ExceptionStore =>
        new ExceptionStoreEngine(new MySqlExceptionalDialect(fixture.ConnectionString), new ExceptionalOptions { ApplicationName = "App" });

    protected override string Quote(string name) => $"`{name}`";

    protected override string VersionTableName =>
        new ThemiaVersionTable(typeof(ExceptionLogMigration).Assembly).TableName;

    protected override void RunMigrations() => ThemiaMigrations.Run(
        MigrationEngine.MySql, fixture.ConnectionString, typeof(ExceptionLogMigration).Assembly);

    protected override string DropUniqueIndexSql => "DROP INDEX `IX_ExceptionAlertState_Exception_Trigger` ON `ExceptionAlertState`";

    [Fact]
    public void Migrations_CanBeReplayedOverAnAlreadyMigratedDatabase()
    {
        var ex = Record.Exception(() => ThemiaMigrations.Run(
            MigrationEngine.MySql, fixture.ConnectionString, typeof(ExceptionLogMigration).Assembly));

        Assert.Null(ex);
    }
}

/// <summary>
/// The same contract with the server's session time zone away from UTC: lease, cap and sent-at must use the UTC
/// clock, so a +07:00 session must not shift them.
/// </summary>
[Collection(MySqlExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class MySqlExceptionAlertStoreNonUtcSessionTests(MySqlExceptionalFixture fixture) : MySqlExceptionAlertStoreTests(fixture)
{
    public override async Task InitializeAsync()
    {
        await Fixture.SetGlobalTimeZoneAsync("+07:00");
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await Fixture.SetGlobalTimeZoneAsync("+00:00");
        await base.DisposeAsync();
    }
}
