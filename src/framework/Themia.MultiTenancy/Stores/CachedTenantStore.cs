using Microsoft.Extensions.Caching.Memory;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Stores;

/// <summary>
/// Caching decorator for ITenantStore using IMemoryCache.
/// Implements ICacheableTenantStore to support cache invalidation.
/// </summary>
public sealed class CachedTenantStore : ICacheableTenantStore
{
    private const string CacheKeyPrefix = "Themia.MultiTenancy.Tenant:";
    private readonly ITenantStore _inner;
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheEntryOptions;
    private readonly HashSet<string> _cachedKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedTenantStore"/> class.
    /// </summary>
    public CachedTenantStore(ITenantStore inner, IMemoryCache cache, TimeSpan? ttl = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cachedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cacheEntryOptions = new MemoryCacheEntryOptions();

        if (ttl.HasValue)
        {
            _cacheEntryOptions.SetAbsoluteExpiration(ttl.Value);
        }

        // Register post-eviction callback to track cache keys
        _cacheEntryOptions.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            if (key is string strKey && strKey.StartsWith(CacheKeyPrefix))
            {
                var identifier = strKey.Substring(CacheKeyPrefix.Length);
                lock (_cachedKeys)
                {
                    _cachedKeys.Remove(identifier);
                }
            }
        });
    }

    /// <inheritdoc />
    public async Task<TenantInfo?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be null or whitespace", nameof(identifier));
        }

        var cacheKey = CacheKeyPrefix + identifier;

        if (_cache.TryGetValue(cacheKey, out TenantInfo? cached))
        {
            return cached;
        }

        var result = await _inner.FindByIdentifierAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (result is not null)
        {
            _cache.Set(cacheKey, result, _cacheEntryOptions);
            lock (_cachedKeys)
            {
                _cachedKeys.Add(identifier);
            }
        }

        return result;
    }

    /// <summary>
    /// Evicts a specific tenant from the cache by identifier.
    /// </summary>
    /// <param name="identifier">The tenant identifier to evict.</param>
    public void EvictFromCache(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return;
        }

        var cacheKey = CacheKeyPrefix + identifier;
        _cache.Remove(cacheKey);
        lock (_cachedKeys)
        {
            _cachedKeys.Remove(identifier);
        }
    }

    /// <summary>
    /// Clears all cached tenants.
    /// </summary>
    public void ClearCache()
    {
        string[] keys;
        lock (_cachedKeys)
        {
            keys = _cachedKeys.ToArray();
            _cachedKeys.Clear();
        }

        foreach (var identifier in keys)
        {
            var cacheKey = CacheKeyPrefix + identifier;
            _cache.Remove(cacheKey);
        }
    }
}
