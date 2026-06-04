namespace Themia.Mediator.Abstractions;

/// <summary>
/// Tracks cache keys to enable efficient bulk invalidation by query type, prefix, or scope.
/// </summary>
public interface ICacheKeyIndex
{
    /// <summary>
    /// Tracks a cache key in the index for future invalidation.
    /// </summary>
    /// <param name="valueKey">The actual cache key storing the value.</param>
    /// <param name="queryType">The query type that generated this cache entry.</param>
    /// <param name="scopeRoot">Optional scope root for automatic invalidation (e.g., "Scope:Order").</param>
    /// <param name="customPrefix">Optional custom prefix from ICacheKeyProvider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TrackAsync(
        string valueKey,
        Type queryType,
        string? scopeRoot,
        string? customPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cached entries for a specific query type.
    /// </summary>
    /// <param name="queryType">The query type whose cache entries should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveByQueryTypeAsync(Type queryType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cached entries that were registered under the specified prefix via
    /// <see cref="TrackAsync"/>. The match is an exact lookup against the prefix that was
    /// registered — it is not a string starts-with scan over raw cache keys.
    /// </summary>
    /// <param name="prefix">
    /// The exact prefix value that was previously passed to <see cref="TrackAsync"/> (matching
    /// <see cref="Themia.Mediator.Abstractions.ICacheKeyProvider.GetCacheKeyPrefix"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cached entries within the specified scope root.
    /// Used for automatic invalidation based on naming conventions.
    /// </summary>
    /// <param name="scopeRoot">The scope root identifier (e.g., "Scope:Order").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveByScopeRootAsync(string scopeRoot, CancellationToken cancellationToken = default);
}
