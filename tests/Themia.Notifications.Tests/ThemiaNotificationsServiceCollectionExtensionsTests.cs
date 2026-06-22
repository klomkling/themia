using Microsoft.Extensions.DependencyInjection;
using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class ThemiaNotificationsServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_RendererAndDefaultSenders()
    {
        var sp = new ServiceCollection().AddLogging().AddThemiaNotifications().BuildServiceProvider();
        Assert.NotNull(sp.GetService<INotificationTemplateRenderer>());
        Assert.NotNull(sp.GetService<IEmailSender>());
        Assert.NotNull(sp.GetService<ISmsSender>());
    }

    [Fact]
    public void Configure_IsApplied_AndIdempotent()
    {
        var services = new ServiceCollection().AddLogging();
        var called = 0;
        services.AddThemiaNotifications(o => { called++; o.ConfigureHandlebars = _ => { }; });
        services.AddThemiaNotifications(); // second call must not duplicate registrations
        var sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<INotificationTemplateRenderer>());
        Assert.Equal(1, called);
    }

    [Fact]
    public void HostSupplied_EmailSender_Wins()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IEmailSender, FakeEmail>();   // host registers first
        services.AddThemiaNotifications();                  // TryAdd must not override
        var sp = services.BuildServiceProvider();
        Assert.IsType<FakeEmail>(sp.GetRequiredService<IEmailSender>());
    }

    private sealed class FakeEmail : IEmailSender
    {
        public Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default) => Task.FromResult(NotificationResult.Success());
    }
}
