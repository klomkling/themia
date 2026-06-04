namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Represents unified caching metadata for a request, combining information from
/// both attributes and interfaces.
/// </summary>
public sealed class CacheMetadata
{
    /// <summary>
    /// Gets a value indicating whether the request is cacheable.
    /// </summary>
    public bool IsCacheable { get; init; }

    /// <summary>
    /// Gets the absolute expiration time for the cached response.
    /// Priority: Attribute value > Interface value > null.
    /// </summary>
    public TimeSpan? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Gets the sliding expiration time for the cached response.
    /// Priority: Attribute value > Interface value > null.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Gets the query types that should be invalidated when this command executes.
    /// Combination of types from both interface and attribute.
    /// </summary>
    public IReadOnlySet<Type> InvalidatedQueryTypes { get; init; } = new HashSet<Type>();

    /// <summary>
    /// Gets the cache key prefixes that should be invalidated when this command executes.
    /// From InvalidatesCacheAttribute.CacheKeyPrefix.
    /// </summary>
    public IReadOnlySet<string> InvalidationPrefixes { get; init; } = new HashSet<string>();

    /// <summary>
    /// Gets a default instance representing no caching metadata.
    /// </summary>
    public static CacheMetadata None { get; } = new()
    {
        IsCacheable = false
    };
}
