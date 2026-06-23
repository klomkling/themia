using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Notifications.Migrations;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

[Trait("Category", "Integration")]
public class SchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync() => await container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Run_CreatesOutboxTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
        Assert.True(await TableExistsAsync("notifications.outbox_messages"));
        Assert.True(await TableExistsAsync("notifications.in_app_notifications"));
        Assert.True(await TableExistsAsync("notifications.notification_preferences"));
        Assert.True(await TableExistsAsync("notifications.tenant_provider_configs"));
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
        var second = Record.Exception(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly));
        Assert.Null(second);
    }

    private async Task<bool> TableExistsAsync(string qualified)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@n) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("n", qualified);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
