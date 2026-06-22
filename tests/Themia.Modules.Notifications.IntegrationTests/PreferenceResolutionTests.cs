using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Migrations;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>
/// Preference resolution against the real EF peer (Postgres) and the real
/// <see cref="INotificationPreferenceStore"/> / <see cref="IPreferenceResolver"/>. Guards the seam between
/// the resolver (which requires the user's rows PLUS the tenant-wide defaults) and the spec that loads them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreferenceResolutionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    // Builds a DI scope with the EF peer registered, the preference store + resolver, and a fixed ambient tenant.
    private AsyncServiceScope BuildScope(TenantId tenant, out ServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaPostgres<TestNotificationsDbContext>(configuration);
        services.AddThemiaDataRepositories<TestNotificationsDbContext>();
        services.AddScoped<INotificationPreferenceStore, NotificationPreferenceStore>();
        services.AddScoped<IPreferenceResolver, PreferenceResolver>();

        provider = services.BuildServiceProvider();
        return provider.CreateAsyncScope();
    }

    private static NotificationPreference Pref(string? userId, NotificationChannel channel, bool enabled, string? locale = null)
    {
        var preference = new NotificationPreference
        {
            UserId = userId,
            Channel = channel,
            IsEnabled = enabled,
            Locale = locale,
        };
        preference.SetId(Guid.NewGuid());
        return preference;
    }

    [Fact]
    public async Task Tenant_default_disable_applies_to_a_user_with_no_row()
    {
        await using var scope = BuildScope(new TenantId("acme"), out var provider);
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationPreferenceStore>();
            var resolver = scope.ServiceProvider.GetRequiredService<IPreferenceResolver>();

            // Tenant-wide default: SMS disabled. The user "no-row-user" has no SMS row of their own.
            await store.UpsertAsync(Pref(userId: null, NotificationChannel.Sms, enabled: false));

            var resolved = await resolver.ResolveAsync(
                "no-row-user", [NotificationChannel.Email, NotificationChannel.Sms]);

            // The tenant default must apply: SMS is NOT enabled, Email (no row) stays enabled.
            Assert.Contains(NotificationChannel.Email, resolved.EnabledChannels);
            Assert.DoesNotContain(NotificationChannel.Sms, resolved.EnabledChannels);
        }
    }

    [Fact]
    public async Task Per_user_row_overrides_a_tenant_default()
    {
        await using var scope = BuildScope(new TenantId("acme"), out var provider);
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationPreferenceStore>();
            var resolver = scope.ServiceProvider.GetRequiredService<IPreferenceResolver>();

            // Tenant default disables SMS; user "userX" re-enables it for themselves.
            await store.UpsertAsync(Pref(userId: null, NotificationChannel.Sms, enabled: false));
            await store.UpsertAsync(Pref(userId: "userX", NotificationChannel.Sms, enabled: true));

            var resolved = await resolver.ResolveAsync(
                "userX", [NotificationChannel.Email, NotificationChannel.Sms]);

            // The user's override wins over the tenant default: SMS IS enabled for userX.
            Assert.Contains(NotificationChannel.Email, resolved.EnabledChannels);
            Assert.Contains(NotificationChannel.Sms, resolved.EnabledChannels);
        }
    }

    [Fact]
    public async Task Tenant_default_locale_applies_when_the_user_has_none()
    {
        await using var scope = BuildScope(new TenantId("acme"), out var provider);
        await using (provider)
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationPreferenceStore>();
            var resolver = scope.ServiceProvider.GetRequiredService<IPreferenceResolver>();

            // Tenant-wide default locale; the resolved user has no preference row.
            await store.UpsertAsync(Pref(userId: null, NotificationChannel.Email, enabled: true, locale: "en-US"));

            var resolved = await resolver.ResolveAsync("locale-less-user", [NotificationChannel.Email]);

            Assert.Equal("en-US", resolved.Locale);
        }
    }
}
