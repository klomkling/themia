using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Notifications.DependencyInjection;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.PostgreSql;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;

using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>
/// Full-path integration test: boots the WHOLE module (neutral senders + module services + dialect
/// + EF peer + tenant context), migrates via <see cref="NotificationsModule.InitializeAsync"/>, then
/// dispatches an email through <see cref="INotificationDispatcher"/>, commits the caller's unit of
/// work, drains the outbox, and asserts the recorded send and the row's <c>sent</c> status. A second
/// test asserts in-app tenant isolation across two tenants (EF peer, Postgres).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DispatchEndToEndTests : IAsyncLifetime
{
    private const int MaxAttempts = 3;

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();
    private bool migrated;

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Dispatch_email_drains_and_marks_the_outbox_row_sent()
    {
        var recorder = new RecordingEmailSender();
        await using var provider = BuildProvider(recorder, new TenantId("acme"));
        await EnsureMigratedAsync(provider);

        // Dispatch one email; the dispatcher stages the outbox row in the caller's unit of work.
        Guid outboxId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await dispatcher.DispatchAsync(
                new NotificationRequest
                {
                    UserId = "user-1",
                    Channels = [NotificationChannel.Email],
                    Recipients = new Dictionary<NotificationChannel, string>
                    {
                        [NotificationChannel.Email] = "to@example.com",
                    },
                    Subject = "Welcome",
                    Body = "Hello there",
                },
                CancellationToken.None);

            await uow.SaveChangesAsync(CancellationToken.None);
            outboxId = await SingleOutboxIdAsync();
        }

        // Kick the drainer's wake signal, then drive one drain cycle (deterministic, no timing race).
        provider.GetRequiredService<DrainSignal>().Signal();
        var drained = await DriveDrainAsync(provider);

        Assert.Equal(1, drained);
        Assert.Single(recorder.Sent);
        Assert.Equal("to@example.com", recorder.Sent[0].Recipient);

        var status = await ReadStatusAsync(outboxId);
        Assert.Equal((int)OutboxStatus.Sent, status); // 2
    }

    [Fact]
    public async Task In_app_notification_is_isolated_per_tenant()
    {
        // Stage + commit an in-app notification as tenant "a".
        Guid id;
        await using (var aProvider = BuildProvider(new RecordingEmailSender(), new TenantId("a")))
        {
            await EnsureMigratedAsync(aProvider);
            await using var scope = aProvider.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await dispatcher.DispatchAsync(
                new NotificationRequest
                {
                    UserId = "shared-user",
                    Channels = [NotificationChannel.InApp],
                    Subject = "Hello",
                    Body = "World",
                },
                CancellationToken.None);

            await uow.SaveChangesAsync(CancellationToken.None);

            var store = scope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            var own = await store.ListForUserAsync("shared-user", unreadOnly: false);
            Assert.Single(own);
            id = own[0].Id;
        }

        // Tenant "b" must not see tenant "a"'s in-app notification, even for the same user id.
        await using (var bProvider = BuildProvider(new RecordingEmailSender(), new TenantId("b")))
        {
            await using var scope = bProvider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            Assert.Empty(await store.ListForUserAsync("shared-user", unreadOnly: false));
        }

        // Sanity: tenant "a" still sees its own row.
        await using (var aProvider = BuildProvider(new RecordingEmailSender(), new TenantId("a")))
        {
            await using var scope = aProvider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            var own = await store.ListForUserAsync("shared-user", unreadOnly: false);
            Assert.Single(own);
            Assert.Equal(id, own[0].Id);
        }
    }

    private ServiceProvider BuildProvider(IEmailSender emailSender, TenantId tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));

        services.AddThemiaPostgres<TestNotificationsDbContext>(configuration);
        services.AddThemiaDataRepositories<TestNotificationsDbContext>();

        // Whole-module wiring: neutral senders + module services + the Postgres dialect.
        services.AddThemiaNotifications();
        // Register the recording sender AFTER the neutral defaults so it wins on resolve.
        services.AddSingleton(emailSender);
        services.AddThemiaNotificationsModule();
        services.AddThemiaNotificationsPostgreSql();

        return services.BuildServiceProvider();
    }

    // Migrate once per container (the schema is shared across the per-tenant providers).
    private async Task EnsureMigratedAsync(ServiceProvider provider)
    {
        if (migrated)
            return;

        await new NotificationsModule(MigrationEngine.Postgres, new NotificationsModuleOptions())
            .InitializeAsync(provider, CancellationToken.None);
        migrated = true;
    }

    // Drives a single drain cycle through the module's registered drainer dependencies.
    private static async Task<int> DriveDrainAsync(ServiceProvider provider)
    {
        var drainer = new OutboxDrainer(
            provider.GetRequiredService<INotificationsSqlDialect>(),
            provider.GetRequiredService<DrainSignal>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NotificationsModuleOptions { MaxAttempts = MaxAttempts, MaxBatchSize = 10 },
            TimeProvider.System,
            NullLogger<OutboxDrainer>.Instance);

        return await drainer.DrainOnceAsync(CancellationToken.None);
    }

    private async Task<Guid> SingleOutboxIdAsync()
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM notifications.outbox_messages", connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "no outbox row staged");
        var id = reader.GetGuid(0);
        Assert.False(await reader.ReadAsync(CancellationToken.None), "more than one outbox row staged");
        return id;
    }

    private async Task<int> ReadStatusAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "SELECT status FROM notifications.outbox_messages WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        Assert.NotNull(result);
        return (int)result!;
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<NotificationMessage> Sent { get; } = [];

        public Task<NotificationResult> SendAsync(
            NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(NotificationResult.Success());
        }
    }
}
