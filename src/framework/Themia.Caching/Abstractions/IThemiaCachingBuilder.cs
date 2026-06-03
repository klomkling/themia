using Microsoft.Extensions.DependencyInjection;

namespace Themia.Caching;

/// <summary>
/// A builder for configuring Themia caching.
/// </summary>
public interface IThemiaCachingBuilder
{
    /// <summary>
    /// Gets the service collection.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Configures memory cache as the caching provider.
    /// </summary>
    /// <param name="configure">Optional action to configure memory cache options.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaCachingBuilder UseMemoryCache(Action<MemoryCacheOptions>? configure = null);

    /// <summary>
    /// Configures Redis/Garnet/Valkey as the caching provider.
    /// </summary>
    /// <param name="connectionString">The Redis connection string.</param>
    /// <param name="configure">Optional action to configure distributed cache options.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaCachingBuilder UseRedis(string connectionString, Action<DistributedCacheOptions>? configure = null);

    /// <summary>
    /// Configures MessagePack as the serialization provider.
    /// </summary>
    /// <returns>The builder for chaining.</returns>
    IThemiaCachingBuilder UseMessagePackSerialization();

    /// <summary>
    /// Configures System.Text.Json as the serialization provider.
    /// </summary>
    /// <param name="configure">Optional action to configure JSON serialization options.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaCachingBuilder UseJsonSerialization(Action<JsonSerializationOptions>? configure = null);

    /// <summary>
    /// Adds a custom cache provider registration.
    /// </summary>
    /// <typeparam name="TProvider">The type of the provider registration.</typeparam>
    /// <param name="lifetime">The service lifetime for the provider.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaCachingBuilder AddProvider<TProvider>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TProvider : class, ICacheProviderRegistration;
}
