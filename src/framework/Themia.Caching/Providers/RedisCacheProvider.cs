using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Themia.Caching;

/// <summary>
/// Distributed cache provider using Redis, Garnet, or Valkey.
/// Thread-safe implementation suitable for multi-instance applications.
/// </summary>
public sealed class RedisCacheProvider : IThemiaCacheProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializationProvider _serializer;
    private readonly string _instanceName;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheProvider"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="serializer">The serialization provider.</param>
    /// <param name="options">The distributed cache options.</param>
    public RedisCacheProvider(
        IConnectionMultiplexer redis,
        ISerializationProvider serializer,
        IOptions<DistributedCacheOptions> options)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

        if (options?.Value is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _instanceName = options.Value.InstanceName ?? string.Empty;
    }

    private IDatabase Database => _redis.GetDatabase();

    private string GetKey(string key) =>
        string.IsNullOrEmpty(_instanceName) ? key : $"{_instanceName}{key}";

    private string GetSlidingMetadataKey(string key) =>
        $"{GetKey(key)}:__sliding";

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetKey(key);
        var bytes = await Database.StringGetAsync(redisKey);

        if (bytes.HasValue)
        {
            return _serializer.Deserialize<T>(bytes!);
        }

        return default;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        CacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
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
        var redisKey = GetKey(key);
        var slidingMetadataKey = GetSlidingMetadataKey(key);

        TimeSpan? expiration = null;
        TimeSpan? slidingExpiration = null;

        if (options is not null)
        {
            // Prefer absolute expiration, fall back to sliding
            if (options.AbsoluteExpiration.HasValue)
            {
                expiration = options.AbsoluteExpiration.Value;
                // Absolute expiration disables sliding metadata
                await Database.KeyDeleteAsync(slidingMetadataKey);
            }
            else if (options.SlidingExpiration.HasValue)
            {
                expiration = options.SlidingExpiration.Value;
                slidingExpiration = options.SlidingExpiration.Value;
            }
            else
            {
                await Database.KeyDeleteAsync(slidingMetadataKey);
            }
        }
        else
        {
            await Database.KeyDeleteAsync(slidingMetadataKey);
        }

        await Database.StringSetAsync(redisKey, bytes, ToExpiration(expiration));

        if (slidingExpiration.HasValue)
        {
            var metadataValue = slidingExpiration.Value.Ticks.ToString(CultureInfo.InvariantCulture);
            await Database.StringSetAsync(
                slidingMetadataKey,
                metadataValue,
                ToExpiration(slidingExpiration));
        }
    }

    // StackExchange.Redis 2.9+ replaced StringSetAsync's TimeSpan? expiry parameter with the
    // Expiration struct (a non-null TimeSpan converts implicitly; null/no-expiry maps to the default).
    private static Expiration ToExpiration(TimeSpan? expiry)
        => expiry.HasValue ? expiry.Value : Expiration.Default;

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetKey(key);
        await Database.KeyDeleteAsync(redisKey);
        await Database.KeyDeleteAsync(GetSlidingMetadataKey(key));
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetKey(key);
        return await Database.KeyExistsAsync(redisKey);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = GetKey(key);
        var slidingMetadataKey = GetSlidingMetadataKey(key);

        // Attempt to refresh based on recorded sliding expiration
        var slidingValue = await Database.StringGetAsync(slidingMetadataKey);
        if (slidingValue.HasValue &&
            long.TryParse(slidingValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
            ticks > 0)
        {
            var slidingExpiration = TimeSpan.FromTicks(ticks);
            await Database.KeyExpireAsync(redisKey, slidingExpiration);
            await Database.KeyExpireAsync(slidingMetadataKey, slidingExpiration);
            return;
        }

        // Fallback to refreshing with the remaining TTL if metadata is unavailable
        var ttl = await Database.KeyTimeToLiveAsync(redisKey);
        if (ttl.HasValue)
        {
            await Database.KeyExpireAsync(redisKey, ttl.Value);
        }
    }
}
