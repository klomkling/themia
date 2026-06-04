using Microsoft.Extensions.Logging;
using Themia.Caching;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Mediator.Abstractions;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Implements cache key indexing using the cache provider to store index sets.
/// Tracks relationships between cache keys for efficient bulk invalidation.
/// </summary>
public sealed class CacheKeyIndex : ICacheKeyIndex
{
    private static readonly TimeSpan LockAcquireTimeout = TimeSpan.FromSeconds(5);

    private readonly IThemiaCacheProvider _cacheProvider;
    private readonly ILogger<CacheKeyIndex> _logger;
    private readonly IDistributedLockProvider _lockProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheKeyIndex"/> class.
    /// </summary>
    /// <param name="cacheProvider">The cache provider for storing index data.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="lockProvider">The distributed lock provider.</param>
    public CacheKeyIndex(
        IThemiaCacheProvider cacheProvider,
        ILogger<CacheKeyIndex> logger,
        IDistributedLockProvider lockProvider)
    {
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
    }

    /// <summary>Returns the tenant prefix for index keys, scoping invalidation to the current tenant.</summary>
    private static string TenantPrefix()
    {
        var tenantId = TenantContextAccessor.CurrentTenantId?.Value;
        return $"t:{tenantId ?? "_"}:";
    }

    /// <inheritdoc />
    public async Task TrackAsync(
        string valueKey,
        Type queryType,
        string? scopeRoot,
        string? customPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueKey);
        ArgumentNullException.ThrowIfNull(queryType);

        try
        {
            var tenant = TenantPrefix();

            // Track by query type
            var typeIndexKey = $"{tenant}Index:Type:{queryType.FullName ?? queryType.Name}";
            await AddToIndexSetAsync(typeIndexKey, valueKey, cancellationToken).ConfigureAwait(false);

            // Track by scope root if provided
            if (!string.IsNullOrWhiteSpace(scopeRoot))
            {
                var scopeIndexKey = $"{tenant}Index:{scopeRoot}";
                await AddToIndexSetAsync(scopeIndexKey, valueKey, cancellationToken).ConfigureAwait(false);
            }

            // Track by custom prefix if provided
            if (!string.IsNullOrWhiteSpace(customPrefix))
            {
                var prefixIndexKey = $"{tenant}Index:Prefix:{customPrefix}";
                await AddToIndexSetAsync(prefixIndexKey, valueKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to track cache key {ValueKey} in index", valueKey);
            // Don't throw - tracking failures should not break the pipeline
        }
    }

    /// <inheritdoc />
    public async Task RemoveByQueryTypeAsync(Type queryType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryType);

        var tenant = TenantPrefix();
        var typeIndexKey = $"{tenant}Index:Type:{queryType.FullName ?? queryType.Name}";
        await RemoveIndexedKeysAsync(typeIndexKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var tenant = TenantPrefix();
        var prefixIndexKey = $"{tenant}Index:Prefix:{prefix}";
        await RemoveIndexedKeysAsync(prefixIndexKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveByScopeRootAsync(string scopeRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);

        var tenant = TenantPrefix();
        var scopeIndexKey = $"{tenant}Index:{scopeRoot}";
        await RemoveIndexedKeysAsync(scopeIndexKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddToIndexSetAsync(string indexKey, string valueKey, CancellationToken cancellationToken)
    {
        var distributedLock = await AcquireLockAsync(indexKey, cancellationToken).ConfigureAwait(false);
        try
        {
            // Get existing set or create new
            var existingSet = await _cacheProvider.GetAsync<HashSet<string>>(indexKey, cancellationToken)
                .ConfigureAwait(false)
                              ?? new HashSet<string>();

            // Add the value key
            existingSet.Add(valueKey);

            // Store back (no expiration for index keys - they're cleaned up on removal)
            await _cacheProvider.SetAsync(indexKey, existingSet, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (distributedLock is not null)
            {
                await distributedLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveIndexedKeysAsync(string indexKey, CancellationToken cancellationToken)
    {
        var distributedLock = await AcquireLockAsync(indexKey, cancellationToken).ConfigureAwait(false);
        try
        {
            // Get the set of value keys
            var valueKeys = await _cacheProvider.GetAsync<HashSet<string>>(indexKey, cancellationToken).ConfigureAwait(false);

            if (valueKeys is null || valueKeys.Count == 0)
            {
                _logger.LogDebug("No cached entries found for index key {IndexKey}", indexKey);
                return;
            }

            _logger.LogDebug("Removing {Count} cached entries for index key {IndexKey}", valueKeys.Count, indexKey);

            // Remove all value keys
            foreach (var valueKey in valueKeys)
            {
                try
                {
                    await _cacheProvider.RemoveAsync(valueKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove cached entry {ValueKey}", valueKey);
                    // Continue removing other keys even if one fails
                }
            }

            // Remove the index key itself
            await _cacheProvider.RemoveAsync(indexKey, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Successfully invalidated cache for index key {IndexKey}", indexKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove indexed keys for {IndexKey}", indexKey);
            // Don't throw - invalidation failures should not break the pipeline
        }
        finally
        {
            if (distributedLock is not null)
            {
                await distributedLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<IDistributedLock?> AcquireLockAsync(string indexKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _lockProvider
                .AcquireAsync($"MediatorCacheIndex:{indexKey}", LockAcquireTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire distributed lock for {IndexKey}. Proceeding without lock.", indexKey);
            return null;
        }
    }
}
