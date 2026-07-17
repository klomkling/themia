#if NET10_0
using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Storage.Migrations;
using Xunit;

namespace Themia.Data.Migrations.Tests;

// Themia.Modules.Storage is net10.0-only (see the csproj TFM condition), so this fixture only compiles
// and runs for that leg of the net8.0;net10.0 test project.
public class StorageMigrationOrderTests
{
    [Fact]
    public void Storage_migrations_are_discovered_in_ascending_version_order()
    {
        // Discovery happens in memory (no DB connection required) — the same guarantee the guard tests
        // above rely on. This proves the runner orders the two real Themia.Storage migrations correctly,
        // ahead of/independent from an actual MigrateUp against a live database.
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString("Host=localhost;Database=x")
                .ScanIn(typeof(StorageSchemaMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = provider.CreateScope();
        var versions = scope.ServiceProvider.GetRequiredService<IMigrationInformationLoader>()
            .LoadMigrations()
            .Keys;

        Assert.Equal([202606170001L, 202607150001L], versions);
    }
}
#endif
