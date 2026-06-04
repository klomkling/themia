namespace Themia.Mediator.Attributes;

/// <summary>
/// Marks a command to invalidate specific query caches upon successful execution.
/// Can specify query types to invalidate and/or a cache key prefix for pattern-based invalidation.
/// When both this attribute and <see cref="Themia.Mediator.Abstractions.ICacheInvalidator"/> are present,
/// the invalidated types from both sources are combined (union).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class InvalidatesCacheAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidatesCacheAttribute"/> class
    /// with the specified query types to invalidate.
    /// </summary>
    /// <param name="queryTypes">The query types whose caches should be invalidated.</param>
    public InvalidatesCacheAttribute(params Type[] queryTypes)
    {
        if (queryTypes is null)
        {
            throw new ArgumentNullException(nameof(queryTypes));
        }

        QueryTypes = queryTypes;
    }

    /// <summary>
    /// Gets the query types whose caches should be invalidated.
    /// </summary>
    public Type[] QueryTypes { get; }

    /// <summary>
    /// Gets or sets an optional registration prefix for targeted invalidation.
    /// When set, the cache index is queried for all entries that were tracked under this exact
    /// prefix (i.e. whose <see cref="Themia.Mediator.Abstractions.ICacheKeyProvider.GetCacheKeyPrefix"/>
    /// returned this value) and removes them. This is an exact-match lookup against the index —
    /// not a string starts-with scan over raw cache keys.
    /// Example: "Order:" removes all cache entries whose provider returned prefix "Order:".
    /// </summary>
    public string? CacheKeyPrefix { get; set; }
}
