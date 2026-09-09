using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

[Collection(PostgresExceptionalCollection.Name)]
public sealed class ExceptionalSchemaProbeTests(PostgresExceptionalFixture fixture)
{
    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheExceptionsTableIsOffTheSearchPath()
    {
        // "Exceptions" is quoted and case-sensitive: probing it unquoted would fold to lower case
        // and report a false negative, so this test also pins the quoting at the call site.
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS exc_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "exc_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaExceptionalPostgres(
                builder.ConnectionString,
                options => options.ApplicationName = "probe-test"))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
