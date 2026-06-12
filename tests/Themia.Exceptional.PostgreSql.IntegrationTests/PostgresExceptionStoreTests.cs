using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Exceptional;
using Themia.Exceptional.Conformance;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.PostgreSql;
using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

[Trait("Category", "Integration")]
public class PostgresExceptionStoreTests : ExceptionStoreConformanceTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new PostgresExceptionalDialect(ConnString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ExceptionLogMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
