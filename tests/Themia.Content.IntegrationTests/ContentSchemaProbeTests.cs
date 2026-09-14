using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Content.Migrations;
using Themia.Content.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Content.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ContentSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheContentTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        await using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            await seed.OpenAsync();
            await using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS content_app";
            await command.ExecuteNonQueryAsync();
        }

        // Migrate on the default search_path, so the tables land in public; then point the application at a schema
        // that cannot see them. The probe, not the migration, is what this test is about.
        ThemiaMigrations.Run(MigrationEngine.Postgres, builder.ConnectionString, typeof(ContentSchemaMigration).Assembly);

        builder.SearchPath = "content_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaContentPostgres(builder.ConnectionString))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
