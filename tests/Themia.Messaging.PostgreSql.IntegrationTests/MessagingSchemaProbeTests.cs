using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Data.Probes;
using Themia.Modules.Messaging.Migrations;

using Xunit;

namespace Themia.Messaging.PostgreSql.IntegrationTests;

public sealed class MessagingSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheOutboxTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS msg_app";
            command.ExecuteNonQuery();
        }

        // Migrate once on the plain (default-search_path) connection string, not the msg_app-scoped one
        // below, for the same reason ChallengesSchemaProbeTests does: MessagingSchemaMigration's index
        // statements are raw Execute.Sql, which follows this connection's actual search_path rather than
        // always landing in 'public' the way Create.Table does. Running the migration itself against a
        // search_path that excludes 'public' therefore fails before the probe this test targets ever
        // runs. Pre-applying the migration here keeps the assertion on the probe.
        ThemiaMigrations.Run(
            MigrationEngine.Postgres, builder.ConnectionString, typeof(MessagingSchemaMigration).Assembly);

        builder.SearchPath = "msg_app";

        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }))
            .ConfigureServices(services => services.AddThemiaMessagingPostgreSql())
            .Build();

        var ex = await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
        Assert.Contains("messaging_outbox_messages", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public", ex.Message, StringComparison.Ordinal);
    }
}
