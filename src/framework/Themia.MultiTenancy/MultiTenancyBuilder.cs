using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Internal;
using Themia.MultiTenancy.Stores;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy;

/// <summary>
/// Fluent builder for configuring multi-tenancy services.
/// </summary>
public sealed class MultiTenancyBuilder
{
    /// <summary>
    /// Gets the underlying service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    internal MultiTenancyBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Adds a custom tenant resolution strategy.
    /// </summary>
    public MultiTenancyBuilder AddStrategy<TStrategy>() where TStrategy : class, ITenantResolutionStrategy
    {
        Services.AddSingleton<ITenantResolutionStrategy, TStrategy>();
        return this;
    }

    /// <summary>
    /// Uses the header strategy (default header: X-Tenant-ID).
    /// </summary>
    public MultiTenancyBuilder UseHeaderStrategy()
    {
        return AddStrategy<HeaderTenantResolutionStrategy>();
    }

    /// <summary>
    /// Uses the path strategy (/{tenantId}/api). Honors options.PathPrefix if provided.
    /// </summary>
    public MultiTenancyBuilder UsePathStrategy()
    {
        return AddStrategy<PathTenantResolutionStrategy>();
    }

    /// <summary>
    /// Uses the default fallback strategy if configured.
    /// </summary>
    public MultiTenancyBuilder UseDefaultStrategy()
    {
        return AddStrategy<DefaultTenantResolutionStrategy>();
    }

    /// <summary>
    /// Seeds the in-memory tenant store with tenants.
    /// </summary>
    public MultiTenancyBuilder SeedTenants(IEnumerable<TenantInfo> tenants)
    {
        Services.RemoveAll<ITenantStore>();
        Services.AddSingleton<ITenantStore>(_ => new InMemoryTenantStore(tenants));
        return this;
    }

    /// <summary>
    /// Wraps the current ITenantStore with a memory cache decorator.
    /// </summary>
    public MultiTenancyBuilder EnableStoreCaching(TimeSpan? ttl = null)
    {
        // Ensure IMemoryCache is available
        Services.TryAddSingleton<IMemoryCache, MemoryCache>();

        var descriptor = Services.LastOrDefault(d => d.ServiceType == typeof(ITenantStore));
        if (descriptor is null)
        {
            throw new InvalidOperationException("No ITenantStore registration found to cache. Register a store before calling EnableStoreCaching().");
        }

        Services.Remove(descriptor);

        Services.Add(new ServiceDescriptor(typeof(ITenantStore), sp =>
        {
            ITenantStore? inner = null;

            if (descriptor.ImplementationInstance is ITenantStore instance)
            {
                inner = instance;
            }
            else if (descriptor.ImplementationFactory is not null)
            {
                inner = descriptor.ImplementationFactory(sp) as ITenantStore;
            }
            else if (descriptor.ImplementationType is not null)
            {
                inner = ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType) as ITenantStore;
            }

            if (inner is null)
            {
                throw new InvalidOperationException("Unable to create inner ITenantStore.");
            }

            var cache = sp.GetRequiredService<IMemoryCache>();
            return new CachedTenantStore(inner, cache, ttl);
        }, descriptor.Lifetime));
        return this;
    }

    /// <summary>
    /// Removes all registered strategies so callers can define an explicit order.
    /// </summary>
    public MultiTenancyBuilder ClearStrategies()
    {
        Services.RemoveAll<ITenantResolutionStrategy>();
        return this;
    }
}

/// <summary>
/// Extension methods for registering Themia multi-tenancy services.
/// </summary>
public static class MultiTenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers multi-tenancy services with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional action to configure MultiTenancyOptions.</param>
    /// <param name="configure">Optional action to configure the MultiTenancyBuilder (add stores, strategies, etc.).</param>
    /// <param name="useDefaultStrategies">
    /// Whether to register default strategies (Header -> Path -> Default).
    /// Set to false to register only custom strategies. Defaults to true.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaMultiTenancy(
        this IServiceCollection services,
        Action<MultiTenancyOptions>? configureOptions = null,
        Action<MultiTenancyBuilder>? configure = null,
        bool? useDefaultStrategies = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        // Determine whether to use default strategies.
        // Priority: explicit parameter > options configuration > default (true).
        // configureOptions is invoked AT MOST ONCE to avoid surprising double-invocation of
        // user-supplied callbacks that may have side effects.
        MultiTenancyOptions? captured = null;
        if (configureOptions is not null)
        {
            captured = new MultiTenancyOptions();
            configureOptions(captured);
        }

        bool shouldUseDefaults = useDefaultStrategies ?? captured?.UseDefaultStrategies ?? true;

        services.AddOptions<MultiTenancyOptions>();
        if (captured is not null)
        {
            // Copy the already-captured values into the options registration; do NOT call
            // configureOptions again here to avoid invoking the user callback a second time.
            var snapshot = captured;
            services.Configure<MultiTenancyOptions>(o =>
            {
                o.HeaderName = snapshot.HeaderName;
                o.PathPrefix = snapshot.PathPrefix;
                o.DefaultTenantIdentifier = snapshot.DefaultTenantIdentifier;
                o.UseDefaultStrategies = snapshot.UseDefaultStrategies;
            });
        }

        // Add options validation
        services.AddSingleton<IValidateOptions<MultiTenancyOptions>, MultiTenancyOptionsValidator>();

        // TenantAccessor is scoped to ensure proper isolation between concurrent requests
        services.TryAddScoped<ITenantAccessor, TenantAccessor>();
        services.TryAddSingleton<ITenantStore, InMemoryTenantStore>();
        services.TryAddSingleton<ITenantResolver, DefaultTenantResolver>();
        services.TryAddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.HttpContextAccessor>();

        var builder = new MultiTenancyBuilder(services);

        // Register default strategies BEFORE calling configure
        // This allows users to add their own strategies in the configure callback
        if (shouldUseDefaults)
        {
            builder.UseHeaderStrategy()
                   .UsePathStrategy()
                   .UseDefaultStrategy();
        }

        // User can add additional strategies or override configuration
        configure?.Invoke(builder);

        return services;
    }
}
