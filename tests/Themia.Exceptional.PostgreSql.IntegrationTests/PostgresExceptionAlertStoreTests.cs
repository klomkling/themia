using Themia.Exceptional.Alerts;
using Themia.Data.Migrations;
using Themia.Exceptional.Conformance;
using Themia.Exceptional.Migrations;

using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

[Collection(PostgresExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class PostgresExceptionAlertStoreTests(PostgresExceptionalFixture fixture) : ExceptionAlertStoreConformanceTests
{
    protected override IExceptionAlertSqlDialect AlertDialect => new PostgresExceptionalDialect(fixture.ConnectionString);

    protected override IExceptionStore ExceptionStore =>
        new ExceptionStoreEngine(new PostgresExceptionalDialect(fixture.ConnectionString), new ExceptionalOptions { ApplicationName = "App" });

    protected override string Quote(string name) => $"\"{name}\"";

    protected override string VersionTableName =>
        new ThemiaVersionTable(typeof(ExceptionLogMigration).Assembly).TableName;

    protected override void RunMigrations() => ThemiaMigrations.Run(
        MigrationEngine.Postgres, fixture.ConnectionString, typeof(ExceptionLogMigration).Assembly);

    protected override string DropUniqueIndexSql => "DROP INDEX \"IX_ExceptionAlertState_Exception_Trigger\"";

    [Fact]
    public void Migrations_CanBeReplayedOverAnAlreadyMigratedDatabase()
    {
        var ex = Record.Exception(() => ThemiaMigrations.Run(
            MigrationEngine.Postgres, fixture.ConnectionString, typeof(ExceptionLogMigration).Assembly));

        Assert.Null(ex);
    }
}
