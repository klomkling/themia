using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Themia.Caching.Internal;

/// <summary>
/// Internal implementation of the caching builder for fluent configuration.
/// </summary>
internal sealed class ThemiaCachingBuilder : IThemiaCachingBuilder
{
    public ThemiaCachingBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IThemiaCachingBuilder UseMemoryCache(Action<MemoryCacheOptions>? configure = null)
    {
        // Always apply configuration so callers can tweak options even if IMemoryCache already exists
        if (configure is not null)
        {
            Services.Configure<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>(opt =>
            {
                var cachingOptions = new MemoryCacheOptions();
                configure(cachingOptions);

                if (cachingOptions.SizeLimit.HasValue)
                {
                    opt.SizeLimit = cachingOptions.SizeLimit.Value;
                }

                if (cachingOptions.CompactionPercentage > 0)
                {
                    opt.CompactionPercentage = cachingOptions.CompactionPercentage;
                }

                if (cachingOptions.ExpirationScanFrequency.HasValue)
                {
                    opt.ExpirationScanFrequency = cachingOptions.ExpirationScanFrequency.Value;
                }
            });
        }

        // Register IMemoryCache if not already registered
        if (!Services.Any(sd => sd.ServiceType == typeof(IMemoryCache)))
        {
            Services.AddMemoryCache();
        }

        // Register MemoryCacheProvider as IThemiaCacheProvider
        Services.AddSingleton<IThemiaCacheProvider, MemoryCacheProvider>();

        return this;
    }

    /// <inheritdoc />
    public IThemiaCachingBuilder UseRedis(string connectionString, Action<DistributedCacheOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Redis connection string cannot be null or empty.", nameof(connectionString));
        }

        // Configure DistributedCacheOptions
        Services.Configure<DistributedCacheOptions>(options =>
        {
            options.ConnectionString = connectionString;
            configure?.Invoke(options);
        });

        // Register IConnectionMultiplexer as singleton
        Services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DistributedCacheOptions>>();
            return ConnectionMultiplexer.Connect(opts.Value.ConnectionString!);
        });

        // Register RedisCacheProvider as IThemiaCacheProvider
        Services.AddSingleton<IThemiaCacheProvider, RedisCacheProvider>();

        return this;
    }

    /// <inheritdoc />
    public IThemiaCachingBuilder UseMessagePackSerialization()
    {
        // Register MessagePackSerializationProvider as ISerializationProvider
        Services.AddSingleton<ISerializationProvider, MessagePackSerializationProvider>();

        return this;
    }

    /// <inheritdoc />
    public IThemiaCachingBuilder UseJsonSerialization(Action<JsonSerializationOptions>? configure = null)
    {
        // Register JsonSerializationProvider with optional configuration
        if (configure is not null)
        {
            Services.AddSingleton<ISerializationProvider>(sp =>
            {
                var options = new JsonSerializationOptions();
                configure(options);
                return new JsonSerializationProvider(options);
            });
        }
        else
        {
            Services.AddSingleton<ISerializationProvider, JsonSerializationProvider>();
        }

        return this;
    }

    /// <inheritdoc />
    public IThemiaCachingBuilder AddProvider<TProvider>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TProvider : class, ICacheProviderRegistration
    {
        Services.Add(new ServiceDescriptor(
            typeof(ICacheProviderRegistration),
            typeof(TProvider),
            lifetime));

        return this;
    }

    internal void ApplyProviderRegistrations(IConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        if (!Services.Any(sd => sd.ServiceType == typeof(ICacheProviderRegistration)))
        {
            return;
        }

        using var serviceProvider = Services.BuildServiceProvider();
        var registrations = serviceProvider
            .GetServices<ICacheProviderRegistration>()
            .OrderByDescending(registration => registration.Priority)
            .ToList();

        foreach (var registration in registrations)
        {
            registration.Register(Services, configuration);
        }
    }
}
