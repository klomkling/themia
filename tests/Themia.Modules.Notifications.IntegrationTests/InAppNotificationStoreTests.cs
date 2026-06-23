using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Migrations;
using Themia.Modules.Notifications.Stores;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>Round-trip + tenant-isolation tests for the in-app notification store (EF peer, Postgres).</summary>
[Trait("Category", "Integration")]
public sealed class InAppNotificationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    // Builds a DI scope with the EF peer registered, the in-app store, and a fixed ambient tenant.
    private AsyncServiceScope BuildScope(TenantId? tenant, out ServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddSingleton(TimeProvider.System);
        services.AddThemiaPostgres<TestNotificationsDbContext>(configuration);
        services.AddThemiaDataRepositories<TestNotificationsDbContext>();
        services.AddScoped<IInAppNotificationStore, InAppNotificationStore>();

        provider = services.BuildServiceProvider();
        return provider.CreateAsyncScope();
    }

    private static InAppNotification NewNotification(string userId) => new()
    {
        UserId = userId,
        Title = "Hello",
        Body = "World",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Add_then_list_returns_the_notification_and_mark_read_flips_the_flag()
    {
        await using var scope = BuildScope(new TenantId("acme"), out var provider);
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();

            var notification = NewNotification("user-1");
            notification.SetId(Guid.CreateVersion7());
            await store.AddAsync(notification);

            var all = await store.ListForUserAsync("user-1", unreadOnly: false);
            Assert.Single(all);
            Assert.Equal(notification.Id, all[0].Id);
            Assert.False(all[0].IsRead);

            var unread = await store.ListForUserAsync("user-1", unreadOnly: true);
            Assert.Single(unread);

            var flipped = await store.MarkReadAsync(notification.Id);
            Assert.True(flipped);

            var afterRead = await store.ListForUserAsync("user-1", unreadOnly: true);
            Assert.Empty(afterRead);

            var readNow = await store.ListForUserAsync("user-1", unreadOnly: false);
            Assert.Single(readNow);
            Assert.True(readNow[0].IsRead);
            Assert.NotNull(readNow[0].ReadAt);
        }
    }

    [Fact]
    public async Task Mark_read_returns_false_for_missing_id()
    {
        await using var scope = BuildScope(new TenantId("acme"), out var provider);
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            Assert.False(await store.MarkReadAsync(Guid.CreateVersion7()));
        }
    }

    [Fact]
    public async Task A_different_tenant_sees_nothing()
    {
        // Write under tenant "a".
        await using (var aScope = BuildScope(new TenantId("a"), out var aProvider))
        await using (aProvider)
        {
            var store = aScope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            var notification = NewNotification("shared-user");
            notification.SetId(Guid.CreateVersion7());
            await store.AddAsync(notification);
            Assert.Single(await store.ListForUserAsync("shared-user", unreadOnly: false));
        }

        // Tenant "b" must not see tenant "a"'s notification, even for the same user id.
        await using (var bScope = BuildScope(new TenantId("b"), out var bProvider))
        await using (bProvider)
        {
            var store = bScope.ServiceProvider.GetRequiredService<IInAppNotificationStore>();
            Assert.Empty(await store.ListForUserAsync("shared-user", unreadOnly: false));
        }
    }
}
