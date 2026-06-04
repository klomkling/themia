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
    /// Gets an optional cache key prefix for pattern-based invalidation.
    /// When provided, commands can invalidate all cached entries with this prefix.
    /// </summary>
    /// <returns>
    /// The cache key prefix (e.g., "Order:", "User:123:") or null if no prefix is needed.
    /// </returns>
    string? GetCacheKeyPrefix() => null;
}
