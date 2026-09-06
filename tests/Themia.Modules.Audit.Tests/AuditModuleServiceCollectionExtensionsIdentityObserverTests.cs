using Microsoft.Extensions.DependencyInjection;
using Themia.Audit;
using Themia.Audit.DependencyInjection;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Modules.Audit.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Xunit;

namespace Themia.Modules.Audit.Tests;

// AddThemiaAuditIdentityObserver must use AddScoped, never TryAdd (design §10a/§11): IIdentityEventObserver
// is resolved as IEnumerable<T>, a fan-out seam, so TryAdd would silently drop this observer whenever an
// adopter had already registered their own — the exact defect the interface exists to correct for
// IAuthenticationHooks and IUserLifecycleHooks.
public class AuditModuleServiceCollectionExtensionsIdentityObserverTests
{
    [Fact]
    public void Registers_the_auditing_observer()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();
        services.AddThemiaAuditIdentityObserver();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var observers = scope.ServiceProvider.GetServices<IIdentityEventObserver>().ToList();
        Assert.Contains(observers, o => o is AuditingIdentityObserver);
    }

    [Fact]
    public void Coexists_with_an_adopters_own_observer_instead_of_replacing_it()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();
        services.AddScoped<IIdentityEventObserver, AdopterObserver>();
        services.AddThemiaAuditIdentityObserver();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var observers = scope.ServiceProvider.GetServices<IIdentityEventObserver>().ToList();
        Assert.Contains(observers, o => o is AuditingIdentityObserver);
        Assert.Contains(observers, o => o is AdopterObserver);
    }

    [Fact]
    public void Registering_the_adopters_observer_after_still_leaves_both_running()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();
        services.AddThemiaAuditIdentityObserver();
        services.AddScoped<IIdentityEventObserver, AdopterObserver>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var observers = scope.ServiceProvider.GetServices<IIdentityEventObserver>().ToList();
        Assert.Equal(2, observers.Count);
    }

    [Fact]
    public void Resolves_from_the_real_DI_graph_under_scope_validation()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();
        services.AddThemiaAuditIdentityObserver();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var observers = scope.ServiceProvider.GetServices<IIdentityEventObserver>().ToList();
        Assert.Single(observers);
        Assert.IsType<AuditingIdentityObserver>(observers[0]);
    }

    private static ServiceCollection BuildWithFakes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditStore>(new FakeAuditStore());
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddScoped<IAmbientConnectionAccessor>(
            _ => new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() });
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        return services;
    }

    // Stands in for an adopter's own observer implementation — never invoked, just resolved.
    private sealed class AdopterObserver : IIdentityEventObserver;
}
