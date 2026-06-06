namespace Themia.Mediator.Configuration;

/// <summary>
/// Configuration options for mediator caching behavior.
/// </summary>
public sealed class MediatorCachingOptions
{
    /// <summary>
    /// Gets or sets the default absolute expiration time for cached entries.
    /// If null, no default absolute expiration is applied unless specified per-request.
    /// </summary>
    public TimeSpan? DefaultAbsoluteExpiration { get; set; }

    /// <summary>
    /// Gets or sets the default sliding expiration time for cached entries.
    /// The cache entry will be removed if not accessed within this timespan.
    /// If null, no default sliding expiration is applied unless specified per-request.
    /// </summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether commands should automatically invalidate
    /// related query caches based on naming conventions (e.g., UpdateOrderCommand invalidates
    /// GetOrderQuery because both strip to the same scope root "Scope:Order").
    /// Note: scope root extraction removes one known suffix and one known verb prefix — there is
    /// no singular/plural normalization, so ListOrdersQuery produces "Scope:Orders" and would NOT
    /// be invalidated by UpdateOrderCommand ("Scope:Order").
    /// Default is true.
    /// </summary>
    public bool EnableAutomaticScopeInvalidation { get; set; } = true;

    /// <summary>
    /// Gets or sets the known type suffixes used for automatic scope invalidation.
    /// These suffixes are used to identify request types and compute their scope roots.
    /// Default values: ["Query", "Command", "Request"]
    /// </summary>
    public IReadOnlyList<string> KnownTypeSuffixes { get; set; } = ["Query", "Command", "Request"];

    /// <summary>
    /// Gets or sets the known verb prefixes used for automatic scope invalidation.
    /// These prefixes are used to extract the entity/resource name from request types.
    /// Default values: ["Get", "List", "Find", "Create", "Update", "Delete", "Remove", "Add", "Set"]
    /// </summary>
    public IReadOnlyList<string> KnownVerbPrefixes { get; set; } =
    [
        "Get",
        "List",
        "Find",
        "Create",
        "Update",
        "Delete",
        "Remove",
        "Add",
        "Set"
    ];
}
