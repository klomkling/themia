namespace Themia.Caching;

/// <summary>
/// Provides a strongly-typed caching abstraction with async operations.
/// </summary>
public interface IThemiaCacheProvider
{
    /// <summary>
    /// Gets a value from the cache.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached value, or null if not found.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Optional cache entry options for expiration and size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the key exists, false otherwise.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the expiration time of a cached entry (if supported by the provider).
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RefreshAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for configuring cache entry expiration and size.
/// </summary>
public sealed class CacheEntryOptions
{
    /// <summary>
    /// Gets or sets the absolute expiration time from now.
    /// </summary>
    public TimeSpan? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Gets or sets the sliding expiration time.
    /// The cache entry will be removed if not accessed within this timespan.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Gets or sets the size of the cache entry (for memory cache size management).
    /// </summary>
    public long? Size { get; init; }
}
