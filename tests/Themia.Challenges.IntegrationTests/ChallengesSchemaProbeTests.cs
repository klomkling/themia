using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Challenges.Migrations;
using Themia.Challenges.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Challenges.IntegrationTests;

public sealed class ChallengesSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheChallengeTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS ch_app";
            command.ExecuteNonQuery();
        }

        // Migrate once on the plain (default-search_path) connection string, not the ch_app-scoped one
        // below. ChallengeSchemaMigration's filtered-unique-index statements are raw, unqualified SQL
        // (FluentMigrator has no fluent syntax for partial/functional indexes -- see the migration's
        // type-level remarks), so unlike its fluent Create.Table/Create.Index calls, they follow this
        // connection's actual search_path rather than always landing in 'public'. Running the migration
        // itself against a search_path that excludes 'public' therefore fails before the probe this test
        // targets ever runs -- a pre-existing property of the already-shipped migration, not something
        // this task may fix (migrations are forward-only; this task only wires up the probe). Pre-applying
        // the migration here keeps the assertion on the probe, which is what this test is for.
        ThemiaMigrations.Run(
            MigrationEngine.Postgres, builder.ConnectionString, typeof(ChallengeSchemaMigration).Assembly);

        builder.SearchPath = "ch_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaChallengesPostgres(builder.ConnectionString))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
