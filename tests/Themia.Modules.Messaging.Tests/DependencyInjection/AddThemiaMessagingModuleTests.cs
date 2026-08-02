using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Themia.Messaging.DependencyInjection;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.DependencyInjection;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.Tests.DependencyInjection;

// Mirrors Themia.Modules.Notifications.Tests.DependencyInjection.AddThemiaNotificationsModuleTests: pins
// that AddThemiaMessagingModule maps MessagingModuleOptions onto OutboxDrainerOptions<ClaimedMessageRow>,
// including a scoped DrainSignal<TRow> per outbox row shape (F4) rather than one shared DrainSignal that
// a Messaging + Notifications host would race over a single wake.
public class AddThemiaMessagingModuleTests
{
    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingIdentity("test-origin");
        services.AddThemiaMessagingModule();
        return services;
    }

    [Theory]
    [InlineData(typeof(IMessageOutboxStore))]
    [InlineData(typeof(DrainSignal<ClaimedMessageRow>))]
    public void AddThemiaMessagingModule_ShouldRegister_ModuleService(Type serviceType)
    {
        var services = BuildServices();

        Assert.Contains(services, d => d.ServiceType == serviceType);
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldRegister_DrainSignal_AsSingleton()
    {
        var services = BuildServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DrainSignal<ClaimedMessageRow>));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldRegister_OutboxDrainer_AsHostedService()
    {
        var services = BuildServices();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && (d.ImplementationType == typeof(OutboxDrainer<ClaimedMessageRow>)
                    || d.ImplementationType?.Name == typeof(OutboxDrainer<ClaimedMessageRow>).Name));
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldReturn_SameServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingIdentity("test-origin");

        var result = services.AddThemiaMessagingModule();

        Assert.Same(services, result);
    }

    // Unlike Notifications (opt-in, existing deployments), Messaging's schema is greenfield: there is no
    // pre-existing history that enabling purge could destroy, so PurgeEnabled defaults to TRUE — do not
    // "fix" this to match Notifications' default.
    [Fact]
    public void AddThemiaMessagingModule_ShouldDefault_PurgeEnabled()
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxDrainerOptions<ClaimedMessageRow>>();

        Assert.True(options.PurgeEnabled);
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldPropagate_DrainAndRetentionSettings()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingIdentity("test-origin");
        services.AddThemiaMessagingModule(o =>
        {
            o.DrainIntervalSeconds = 9;
            o.MaxBatchSize = 17;
            o.MaxAttempts = 4;
            o.LeaseSeconds = 33;
            o.PurgeEnabled = false;
            o.SentRetentionDays = 3;
            o.DeadRetentionDays = 45;
        });
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxDrainerOptions<ClaimedMessageRow>>();

        Assert.Equal(9, options.DrainIntervalSeconds);
        Assert.Equal(17, options.MaxBatchSize);
        Assert.Equal(4, options.MaxAttempts);
        Assert.Equal(33, options.LeaseSeconds);
        Assert.False(options.PurgeEnabled);
        Assert.Equal(3, options.SentRetentionDays);
        Assert.Equal(45, options.DeadRetentionDays);
    }
}
