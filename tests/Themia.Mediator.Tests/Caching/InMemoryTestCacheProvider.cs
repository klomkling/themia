using System.Collections.Concurrent;
using Themia.Caching;

namespace Themia.Mediator.Tests.Caching;

/// <summary>
/// In-memory cache provider for testing purposes.
/// </summary>
public sealed class InMemoryTestCacheProvider : IThemiaCacheProvider
{
    private readonly ConcurrentDictionary<string, (object Value, DateTimeOffset? AbsoluteExpiration, TimeSpan? SlidingExpiration)> _cache = new();

    public bool SimulateFailure { get; set; }
    public int GetCallCount { get; private set; }
    public int SetCallCount { get; private set; }
    public int RemoveCallCount { get; private set; }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        GetCallCount++;

        if (SimulateFailure)
            throw new InvalidOperationException("Simulated cache failure");

        if (_cache.TryGetValue(key, out var entry))
        {
            // Check if expired
            if (entry.AbsoluteExpiration.HasValue && DateTimeOffset.UtcNow > entry.AbsoluteExpiration.Value)
            {
                _cache.TryRemove(key, out _);
                return Task.FromResult<T?>(default);
            }

            return Task.FromResult((T?)entry.Value);
        }

        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
    {
        SetCallCount++;

        if (SimulateFailure)
            throw new InvalidOperationException("Simulated cache failure");

        DateTimeOffset? absoluteExpiration = null;
        if (options?.AbsoluteExpiration.HasValue == true)
        {
            absoluteExpiration = DateTimeOffset.UtcNow.Add(options.AbsoluteExpiration.Value);
        }

        _cache[key] = (value!, absoluteExpiration, options?.SlidingExpiration);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        RemoveCallCount++;

        if (SimulateFailure)
            throw new InvalidOperationException("Simulated cache failure");

        _cache.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (SimulateFailure)
            throw new InvalidOperationException("Simulated cache failure");

        return Task.FromResult(_cache.ContainsKey(key));
    }

    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (SimulateFailure)
            throw new InvalidOperationException("Simulated cache failure");

        // For in-memory testing, refresh is a no-op
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _cache.Clear();
        GetCallCount = 0;
        SetCallCount = 0;
        RemoveCallCount = 0;
    }
}
