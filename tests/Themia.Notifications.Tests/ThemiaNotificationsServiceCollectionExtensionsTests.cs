using Microsoft.Extensions.DependencyInjection;
using Themia.Notifications;
using Themia.Notifications.Providers;
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

    [Fact]
    public void AddThemiaSmtpEmailSender_MakesSmtpTheEmailSender()
    {
        var sp = new ServiceCollection().AddLogging()
            .AddThemiaNotifications()
            .AddThemiaSmtpEmailSender(o => { o.Host = "localhost"; o.FromAddress = "x@y.z"; })
            .BuildServiceProvider();

        Assert.IsType<SmtpEmailSender>(sp.GetRequiredService<IEmailSender>());
        Assert.Single(sp.GetServices<IEmailSender>());   // Replace removed the logger default
    }

    [Fact]
    public void AddThemiaSmtpEmailSender_AloneResolvesSmtpSender()
    {
        // Without a prior AddThemiaNotifications, the renderer must still be present.
        var sp = new ServiceCollection().AddLogging()
            .AddThemiaSmtpEmailSender(o => { o.Host = "localhost"; o.FromAddress = "x@y.z"; })
            .BuildServiceProvider();

        Assert.IsType<Themia.Notifications.Providers.SmtpEmailSender>(sp.GetRequiredService<IEmailSender>());
        Assert.NotNull(sp.GetRequiredService<INotificationTemplateRenderer>());
    }

    [Fact]
    public void AddThemiaSmtpEmailSender_RejectsEmptyFromAddress()
        => Assert.Throws<System.ArgumentException>(() =>
            new ServiceCollection().AddLogging().AddThemiaSmtpEmailSender(o => { o.Host = "smtp.test"; /* no FromAddress */ }));

    private sealed class FakeEmail : IEmailSender
    {
        public Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default) => Task.FromResult(NotificationResult.Success());
    }
}
