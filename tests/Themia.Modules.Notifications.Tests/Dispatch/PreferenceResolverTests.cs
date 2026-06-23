using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Dispatch;

public class PreferenceResolverTests
{
    private sealed class FakePreferenceStore(IReadOnlyList<NotificationPreference> all) : INotificationPreferenceStore
    {
        public Task<IReadOnlyList<NotificationPreference>> ListAsync(string? userId, CancellationToken ct = default)
        {
            // Mirrors the real store: a user query returns that user's rows plus the tenant-wide defaults (null UserId).
            IReadOnlyList<NotificationPreference> result = userId is null
                ? all
                : all.Where(p => p.UserId == userId || p.UserId is null).ToList();
            return Task.FromResult(result);
        }

        public Task UpsertAsync(NotificationPreference preference, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static NotificationPreference Pref(string? userId, NotificationChannel channel, bool enabled, string? locale = null)
        => new() { UserId = userId, Channel = channel, IsEnabled = enabled, Locale = locale };

    [Fact]
    public async Task Resolve_for_user_who_re_enables_a_tenant_disabled_channel_keeps_it()
    {
        // Tenant default disables SMS; user X re-enables it.
        var store = new FakePreferenceStore(
        [
            Pref(userId: null, NotificationChannel.Sms, enabled: false),
            Pref(userId: "X", NotificationChannel.Sms, enabled: true),
        ]);
        var resolver = new PreferenceResolver(store);

        var resolved = await resolver.ResolveAsync(
            "X", [NotificationChannel.Email, NotificationChannel.Sms]);

        Assert.Contains(NotificationChannel.Email, resolved.EnabledChannels);
        Assert.Contains(NotificationChannel.Sms, resolved.EnabledChannels);
        Assert.Equal(2, resolved.EnabledChannels.Count);
    }

    [Fact]
    public async Task Resolve_for_user_without_override_inherits_tenant_disable()
    {
        var store = new FakePreferenceStore(
        [
            Pref(userId: null, NotificationChannel.Sms, enabled: false),
            Pref(userId: "X", NotificationChannel.Sms, enabled: true),
        ]);
        var resolver = new PreferenceResolver(store);

        var resolved = await resolver.ResolveAsync(
            "Y", [NotificationChannel.Email, NotificationChannel.Sms]);

        Assert.Contains(NotificationChannel.Email, resolved.EnabledChannels);
        Assert.DoesNotContain(NotificationChannel.Sms, resolved.EnabledChannels);
        Assert.Single(resolved.EnabledChannels);
    }

    [Fact]
    public async Task Resolve_keeps_channel_with_no_row_enabled_opt_out_model()
    {
        var store = new FakePreferenceStore([]);
        var resolver = new PreferenceResolver(store);

        var resolved = await resolver.ResolveAsync(
            "Z", [NotificationChannel.Email, NotificationChannel.Sms]);

        Assert.Equal(2, resolved.EnabledChannels.Count);
    }

    [Fact]
    public async Task Resolve_locale_prefers_user_row_over_tenant_row()
    {
        var store = new FakePreferenceStore(
        [
            Pref(userId: null, NotificationChannel.Email, enabled: true, locale: "en-US"),
            Pref(userId: "X", NotificationChannel.Email, enabled: true, locale: "th-TH"),
        ]);
        var resolver = new PreferenceResolver(store);

        var resolved = await resolver.ResolveAsync("X", [NotificationChannel.Email]);

        Assert.Equal("th-TH", resolved.Locale);
    }

    [Fact]
    public async Task Resolve_locale_falls_back_to_tenant_row_when_user_has_none()
    {
        var store = new FakePreferenceStore(
        [
            Pref(userId: null, NotificationChannel.Email, enabled: true, locale: "en-US"),
        ]);
        var resolver = new PreferenceResolver(store);

        var resolved = await resolver.ResolveAsync("Y", [NotificationChannel.Email]);

        Assert.Equal("en-US", resolved.Locale);
    }
}
