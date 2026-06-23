using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Migrations;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>Rollback-safety tests for the transactional outbox store (EF peer, Postgres).</summary>
[Trait("Category", "Integration")]
public sealed class OutboxEnqueueTransactionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    // Builds a DI scope with the EF peer registered, the outbox store, and a fixed ambient tenant.
    private AsyncServiceScope BuildScope(out ServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaPostgres<TestNotificationsDbContext>(configuration);
        services.AddThemiaDataRepositories<TestNotificationsDbContext>();
        services.AddScoped<IOutboxStore, OutboxStore>();

        provider = services.BuildServiceProvider();
        return provider.CreateAsyncScope();
    }

    private static OutboxMessage NewEmail(string recipient)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new OutboxMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = recipient,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        };
        message.SetId(Guid.NewGuid());
        return message;
    }

    private async Task<long> CountOutboxAsync()
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM notifications.outbox_messages", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Enqueue_without_save_does_not_persist()
    {
        await using (var scope = BuildScope(out var provider))
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await store.EnqueueAsync(NewEmail("a@example.com"));
            // No SaveChanges -> disposing the scope discards the staged insert.
        }

        Assert.Equal(0, await CountOutboxAsync());
    }

    [Fact]
    public async Task Enqueue_then_save_persists_the_row()
    {
        await using (var scope = BuildScope(out var provider))
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await store.EnqueueAsync(NewEmail("a@example.com"));
            await uow.SaveChangesAsync();
        }

        Assert.Equal(1, await CountOutboxAsync());
    }
}
