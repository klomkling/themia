using Testcontainers.MySql;
using Themia.Data.Migrations;
using Themia.Exceptional;
using Themia.Exceptional.Conformance;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.MySql;
using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

[Trait("Category", "Integration")]
public class MySqlExceptionStoreTests : ExceptionStoreConformanceTests, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    // No GuidFormat suffix: MySqlExceptionalDialect applies GuidFormat=Char36 itself, so a plain
    // connection string round-trips System.Guid ↔ CHAR(36) — this exercises that behavior.
    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new MySqlExceptionalDialect(ConnString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.MySql, ConnString, typeof(ExceptionLogMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
