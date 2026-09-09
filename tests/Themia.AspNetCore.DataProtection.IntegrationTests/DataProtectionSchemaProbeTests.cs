using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Themia.AspNetCore.DataProtection.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.AspNetCore.DataProtection.IntegrationTests;

[Collection(PostgresDataProtectionCollection.Name)]
public sealed class DataProtectionSchemaProbeTests(PostgresDataProtectionFixture fixture)
{
    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheKeyTableIsOffTheSearchPath()
    {
        // The migration creates public.data_protection_keys; the app then runs on a search_path
        // that does not include public. Today this surfaces on the first protector, not at boot.
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var migrationConnectionString = builder.ConnectionString;

        using (var seed = new NpgsqlConnection(migrationConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS dp_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "dp_app";
        var appConnectionString = builder.ConnectionString;

        using var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddDataProtection()
                .SetApplicationName("probe-test")
                .PersistKeysToThemiaPostgres(appConnectionString, runMigration: false))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
