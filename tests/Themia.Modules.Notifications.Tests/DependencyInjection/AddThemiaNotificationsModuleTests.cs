using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Modules.Notifications;
using Themia.Modules.Notifications.Config;
using Themia.Modules.Notifications.DependencyInjection;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.Stores;
using Xunit;

namespace Themia.Modules.Notifications.Tests.DependencyInjection;

public class AddThemiaNotificationsModuleTests
{
    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddThemiaNotificationsModule(o => o.ConnectionStringName = "X");
        return services;
    }

    [Theory]
    [InlineData(typeof(INotificationDispatcher))]
    [InlineData(typeof(IOutboxStore))]
    [InlineData(typeof(IInAppNotificationStore))]
    [InlineData(typeof(INotificationPreferenceStore))]
    [InlineData(typeof(ITenantProviderConfigStore))]
    [InlineData(typeof(IPreferenceResolver))]
    [InlineData(typeof(IProviderConfigResolver))]
    [InlineData(typeof(DrainSignal))]
    public void AddThemiaNotificationsModule_ShouldRegister_ModuleService(Type serviceType)
    {
        var services = BuildServices();

        Assert.Contains(services, d => d.ServiceType == serviceType);
    }

    [Fact]
    public void AddThemiaNotificationsModule_ShouldRegister_DrainSignal_AsSingleton()
    {
        var services = BuildServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DrainSignal));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddThemiaNotificationsModule_ShouldRegister_OutboxDrainer_AsHostedService()
    {
        var services = BuildServices();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && (d.ImplementationType == typeof(OutboxDrainer)
                    || d.ImplementationType?.Name == nameof(OutboxDrainer)));
    }

    [Fact]
    public void AddThemiaNotificationsModule_ShouldNotRegister_SqlDialect()
    {
        var services = BuildServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INotificationsSqlDialect));
    }

    [Fact]
    public void AddThemiaNotificationsModule_ShouldReturn_SameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddThemiaNotificationsModule(o => o.ConnectionStringName = "X");

        Assert.Same(services, result);
    }

    [Fact]
    public void NotificationsModuleOptions_Validate_ShouldThrow_WhenConnectionStringNameBlank()
    {
        var options = new NotificationsModuleOptions { ConnectionStringName = " " };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
