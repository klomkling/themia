using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Probes;

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

        builder.SearchPath = "msg_app";

        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }))
            .ConfigureServices(services => services.AddThemiaMessagingPostgreSql())
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
