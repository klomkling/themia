using Microsoft.Extensions.Caching.Memory;

namespace Themia.Caching;

/// <summary>
/// In-memory cache provider using Microsoft.Extensions.Caching.Memory.
/// Thread-safe implementation suitable for single-instance applications.
/// </summary>
public sealed class MemoryCacheProvider : IThemiaCacheProvider
{
    private readonly IMemoryCache _cache;
    private readonly ISerializationProvider _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCacheProvider"/> class.
    /// </summary>
    /// <param name="cache">The memory cache instance.</param>
    /// <param name="serializer">The serialization provider.</param>
    public MemoryCacheProvider(IMemoryCache cache, ISerializationProvider serializer)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_cache.TryGetValue(key, out byte[]? bytes) && bytes is not null)
        {
            var value = _serializer.Deserialize<T>(bytes);
            return Task.FromResult(value);
        }

        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var bytes = _serializer.Serialize(value);

        var cacheEntryOptions = new MemoryCacheEntryOptions();

        if (options is not null)
        {
            if (options.AbsoluteExpiration.HasValue)
            {
                cacheEntryOptions.SetAbsoluteExpiration(options.AbsoluteExpiration.Value);
            }

            if (options.SlidingExpiration.HasValue)
            {
                cacheEntryOptions.SetSlidingExpiration(options.SlidingExpiration.Value);
            }

            if (options.Size.HasValue)
            {
                cacheEntryOptions.SetSize(options.Size.Value);
            }
            else
            {
                // Default size of 1 when not specified (required when cache has SizeLimit)
                cacheEntryOptions.SetSize(1);
            }
        }
        else
        {
            // Default size of 1 when no options provided (required when cache has SizeLimit)
            cacheEntryOptions.SetSize(1);
        }

        _cache.Set(key, bytes, cacheEntryOptions);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        _cache.Remove(key);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var exists = _cache.TryGetValue(key, out _);

        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // For memory cache, TryGetValue already refreshes sliding expiration
        // So we just need to access the entry
        _cache.TryGetValue(key, out _);

        return Task.CompletedTask;
    }
}
