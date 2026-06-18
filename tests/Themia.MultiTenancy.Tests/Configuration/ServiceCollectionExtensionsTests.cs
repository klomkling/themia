using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;
using Themia.MultiTenancy.Stores;

namespace Themia.MultiTenancy.Tests.Configuration;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddThemiaMultiTenancy_WithoutOptions_ShouldRegisterDefaultServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<ITenantAccessor>());
        Assert.NotNull(provider.GetService<ITenantStore>());
        Assert.NotNull(provider.GetService<ITenantResolver>());
    }

    [Fact]
    public void AddThemiaMultiTenancy_ShouldRegisterDefaultStrategies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>().ToList();

        Assert.Contains(strategies, s => s is HeaderTenantResolutionStrategy);
        Assert.Contains(strategies, s => s is PathTenantResolutionStrategy);
        Assert.Contains(strategies, s => s is DefaultTenantResolutionStrategy);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithClearStrategies_ShouldRemoveDefaultStrategies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder =>
        {
            builder.ClearStrategies();
        });

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Empty(strategies);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithUseDefaultStrategiesFalse_ViaOptions_ShouldNotRegisterDefaultStrategies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(options =>
        {
            options.UseDefaultStrategies = false;
        });

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Empty(strategies);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithUseDefaultStrategiesFalse_ViaParameter_ShouldNotRegisterDefaultStrategies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(useDefaultStrategies: false);

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Empty(strategies);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithUseDefaultStrategiesFalse_ShouldPreserveCustomStrategies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(
            options =>
            {
                options.UseDefaultStrategies = false;
            },
            configure: builder =>
            {
                builder.AddStrategy<TestCustomStrategy>();
            });

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>().ToList();

        // Should have exactly 1 strategy (the custom one)
        var strategy = Assert.Single(strategies);
        Assert.IsType<TestCustomStrategy>(strategy);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithDefaultStrategiesAndCustom_ShouldRegisterBoth()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder =>
        {
            builder.AddStrategy<TestCustomStrategy>();
        });

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>().ToList();

        // Should have 3 defaults + 1 custom = 4 total
        Assert.Equal(4, strategies.Count);
        Assert.Contains(strategies, s => s is HeaderTenantResolutionStrategy);
        Assert.Contains(strategies, s => s is PathTenantResolutionStrategy);
        Assert.Contains(strategies, s => s is DefaultTenantResolutionStrategy);
        Assert.Contains(strategies, s => s is TestCustomStrategy);
    }

    [Fact]
    public void AddThemiaMultiTenancy_StrategyOrdering_DefaultsFirst_ThenCustom()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder =>
        {
            builder.AddStrategy<TestCustomStrategy>();
        });

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>().ToArray();

        // Defaults should be registered first (order matters for resolution)
        Assert.IsType<HeaderTenantResolutionStrategy>(strategies[0]);
        Assert.IsType<PathTenantResolutionStrategy>(strategies[1]);
        Assert.IsType<DefaultTenantResolutionStrategy>(strategies[2]);
        Assert.IsType<TestCustomStrategy>(strategies[3]);
    }

    [Fact]
    public void AddThemiaMultiTenancy_ParameterOverridesOptions_WhenBothProvided()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Options says true, but parameter says false - parameter should win
        services.AddThemiaMultiTenancy(
            options =>
            {
                options.UseDefaultStrategies = true;
            },
            useDefaultStrategies: false);

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Empty(strategies);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithCustomOptions_ShouldApplyConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(options =>
        {
            options.HeaderName = "X-Custom-Tenant";
            options.DefaultTenantIdentifier = "default-tenant";
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiTenancyOptions>>();

        Assert.Equal("X-Custom-Tenant", options.Value.HeaderName);
        Assert.Equal("default-tenant", options.Value.DefaultTenantIdentifier);
    }

    [Fact]
    public async Task AddThemiaMultiTenancy_WithBuilder_ShouldApplyBuilderConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder =>
        {
            builder.SeedTenants(new[] { new TenantInfo("tenant-1", "acme") });
        });

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ITenantStore>();

        var tenant = await store.FindByIdentifierAsync("acme");
        Assert.NotNull(tenant);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithNullServices_ShouldThrow()
    {
        ServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(() =>
        {
            services!.AddThemiaMultiTenancy();
        });
    }

    [Fact]
    public void AddThemiaMultiTenancy_ShouldRegisterInMemoryStoreByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ITenantStore>();

        Assert.IsType<InMemoryTenantStore>(store);
    }

    [Fact]
    public void AddThemiaMultiTenancy_ShouldRegisterHttpContextAccessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

        Assert.NotNull(accessor);
    }

    [Fact]
    public void AddThemiaMultiTenancy_MultipleCalls_ShouldNotDuplicateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();
        services.AddThemiaMultiTenancy();

        var accessorDescriptors = services.Where(s => s.ServiceType == typeof(ITenantAccessor)).ToList();
        Assert.Single(accessorDescriptors);
    }

    [Fact]
    public void AddThemiaMultiTenancy_DoesNotRegisterClaimsStrategy_ByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.DoesNotContain(strategies, s => s is ClaimsTenantResolutionStrategy);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithUseClaimsStrategy_ShouldRegisterClaimsStrategy()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder => builder.UseClaimsStrategy());

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Contains(strategies, s => s is ClaimsTenantResolutionStrategy);
    }
}

/// <summary>
/// Test strategy for validating custom strategy registration.
/// </summary>
internal sealed class TestCustomStrategy : ITenantResolutionStrategy
{
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TenantResolutionResult.NotFound("test-custom", "Test strategy"));
    }
}
