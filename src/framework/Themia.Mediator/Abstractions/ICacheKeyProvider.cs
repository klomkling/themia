namespace Themia.Mediator.Abstractions;

/// <summary>
/// Allows a request to provide a custom cache key instead of using the default generated key.
/// </summary>
public interface ICacheKeyProvider
{
    /// <summary>
    /// Gets the custom cache key for this request.
    /// </summary>
    /// <returns>The cache key to use for storing/retrieving this request's response.</returns>
    string GetCacheKey();

    /// <summary>
    /// Gets an optional cache key prefix used as a registration bucket for targeted invalidation.
    /// When provided, all cache entries produced by this request are tracked under this prefix
    /// in the index; a command can then remove that entire bucket by passing the exact same value
    /// to <see cref="ICacheKeyIndex.RemoveByPrefixAsync"/>. The match is an exact lookup — not a
    /// string starts-with scan over raw cache keys.
    /// </summary>
    /// <returns>
    /// The registration prefix (e.g., "Order:", "User:123:") or null if no prefix tracking is needed.
    /// </returns>
    string? GetCacheKeyPrefix() => null;
}
