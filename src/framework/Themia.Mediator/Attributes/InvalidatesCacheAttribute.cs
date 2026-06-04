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
    /// Gets or sets an optional cache key prefix for pattern-based invalidation.
    /// When set, all cache entries with keys starting with this prefix will be invalidated.
    /// Example: "Order:" will invalidate all Order-related caches.
    /// </summary>
    public string? CacheKeyPrefix { get; set; }
}
