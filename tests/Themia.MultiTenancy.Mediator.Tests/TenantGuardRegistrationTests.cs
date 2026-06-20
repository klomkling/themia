using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Mediator.Abstractions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Mediator;
using Xunit;

namespace Themia.MultiTenancy.Mediator.Tests;

public class TenantGuardRegistrationTests
{
    [Fact]
    public void AddThemiaTenantGuard_RegistersOpenGenericBehavior()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(TenantGuardBehavior<,>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddThemiaTenantGuard_WithConfigure_SetsPrivilegedRoles()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard(o => o.PrivilegedRoles = ["SaaSAdmin"]);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TenantGuardOptions>>().Value;
        Assert.Contains("SaaSAdmin", options.PrivilegedRoles);
    }

    [Fact]
    public void AddThemiaTenantGuard_WithoutConfigure_DefaultsToEmptyPrivilegedRoles()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TenantGuardOptions>>().Value;
        Assert.Empty(options.PrivilegedRoles);
    }

    [Fact]
    public void AddThemiaTenantGuard_CalledTwice_RegistersBehaviorOnce()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard();
        services.AddThemiaTenantGuard();

        Assert.Single(services, d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(TenantGuardBehavior<,>));
    }
}
