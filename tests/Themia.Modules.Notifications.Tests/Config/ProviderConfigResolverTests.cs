using Themia.Modules.Notifications.Config;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Config;

public class ProviderConfigResolverTests
{
    private sealed class FakeProviderConfigStore(TenantProviderConfig? emailConfig) : ITenantProviderConfigStore
    {
        public Task<TenantProviderConfig?> FindAsync(NotificationChannel channel, CancellationToken ct = default)
            => Task.FromResult(channel == NotificationChannel.Email ? emailConfig : null);

        public Task UpsertAsync(TenantProviderConfig config, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Resolve_returns_the_stored_config_when_a_row_exists()
    {
        var config = new TenantProviderConfig { Channel = NotificationChannel.Email, Host = "smtp.example.com" };
        var resolver = new ProviderConfigResolver(new FakeProviderConfigStore(config));

        var resolved = await resolver.ResolveAsync(NotificationChannel.Email);

        Assert.Same(config, resolved);
    }

    [Fact]
    public async Task Resolve_returns_null_when_no_row_exists_so_caller_falls_back_to_global_config()
    {
        var resolver = new ProviderConfigResolver(new FakeProviderConfigStore(emailConfig: null));

        var resolved = await resolver.ResolveAsync(NotificationChannel.Sms);

        Assert.Null(resolved);
    }
}
