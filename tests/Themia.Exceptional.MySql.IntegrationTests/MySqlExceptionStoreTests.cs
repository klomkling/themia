using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MySql;
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
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddMySql8().WithGlobalConnectionString(ConnString)
                .ScanIn(typeof(ExceptionLogMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
