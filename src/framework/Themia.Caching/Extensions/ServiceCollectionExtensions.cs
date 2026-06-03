using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Caching.Internal;

namespace Themia.Caching.Extensions;

/// <summary>
/// Extension methods for configuring Themia caching services.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    /// <summary>
    /// Adds Themia caching with default configuration (MemoryCache + MessagePack).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaCaching(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        var builder = new ThemiaCachingBuilder(services);

        // Default configuration: MemoryCache + MessagePack
        builder
            .UseMemoryCache()
            .UseMessagePackSerialization();

        builder.ApplyProviderRegistrations(EmptyConfiguration);

        return services;
    }

    /// <summary>
    /// Adds Themia caching with custom configuration using a builder action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The configuration action for the caching builder.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaCaching(
        this IServiceCollection services,
        Action<IThemiaCachingBuilder> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new ThemiaCachingBuilder(services);
        configure(builder);

        builder.ApplyProviderRegistrations(EmptyConfiguration);

        return services;
    }

    /// <summary>
    /// Adds Themia caching with configuration from IConfiguration (appsettings.json).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration containing caching settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var section = configuration.GetSection(CachingOptions.SectionName);
        return ConfigureFromSection(services, section);
    }

    /// <summary>
    /// Adds Themia caching with a specific configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="sectionName">The section name containing caching settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("Section name cannot be null or whitespace.", nameof(sectionName));
        }

        var section = configuration.GetSection(sectionName);
        return ConfigureFromSection(services, section);
    }

    private static IServiceCollection ConfigureFromSection(
        IServiceCollection services,
        IConfiguration section)
    {
        var builder = new ThemiaCachingBuilder(services);

        // Bind configuration to CachingOptions
        var options = new CachingOptions();
        section.Bind(options);

        // Configure serialization first (default to MessagePack if not specified)
        if (string.Equals(options.Serialization.Provider, "Json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Serialization.Provider, "SystemTextJson", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseJsonSerialization();
        }
        else
        {
            builder.UseMessagePackSerialization();
        }

        // Configure cache provider based on options
        // Prefer distributed cache if enabled, otherwise use memory cache
        if (options.DistributedCache.Enabled &&
            !string.IsNullOrWhiteSpace(options.DistributedCache.ConnectionString))
        {
            builder.UseRedis(options.DistributedCache.ConnectionString, opt =>
            {
                opt.InstanceName = options.DistributedCache.InstanceName;
                opt.Provider = options.DistributedCache.Provider;
            });
        }
        else if (options.MemoryCache.Enabled)
        {
            builder.UseMemoryCache(opt =>
            {
                opt.SizeLimit = options.MemoryCache.SizeLimit;
                opt.CompactionPercentage = options.MemoryCache.CompactionPercentage;
                opt.ExpirationScanFrequency = options.MemoryCache.ExpirationScanFrequency;
            });
        }
        else
        {
            // Default to memory cache if nothing is explicitly enabled
            builder.UseMemoryCache();
        }

        builder.ApplyProviderRegistrations(section);

        return services;
    }
}
