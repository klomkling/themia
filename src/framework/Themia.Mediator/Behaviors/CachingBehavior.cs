using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Caching;
using Themia.Logging;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Configuration;
using Themia.Mediator.Infrastructure;
using Themia.Mediator.Pipelines;

namespace Themia.Mediator.Behaviors;

/// <summary>
/// Pipeline behavior that provides caching for queries and automatic/manual cache invalidation for commands.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;
    private readonly IThemiaCacheProvider _cacheProvider;
    private readonly ICacheKeyFactory _keyFactory;
    private readonly ICacheMetadataProvider _metadataProvider;
    private readonly ICacheKeyIndex _keyIndex;
    private readonly MediatorCachingOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheProvider">The cache provider.</param>
    /// <param name="keyFactory">The cache key factory.</param>
    /// <param name="metadataProvider">The cache metadata provider.</param>
    /// <param name="keyIndex">The cache key index.</param>
    /// <param name="options">The caching options.</param>
    public CachingBehavior(
        ILogger<CachingBehavior<TRequest, TResponse>> logger,
        IThemiaCacheProvider cacheProvider,
        ICacheKeyFactory keyFactory,
        ICacheMetadataProvider metadataProvider,
        ICacheKeyIndex keyIndex,
        IOptions<MediatorCachingOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
        _keyFactory = keyFactory ?? throw new ArgumentNullException(nameof(keyFactory));
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _keyIndex = keyIndex ?? throw new ArgumentNullException(nameof(keyIndex));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var requestType = typeof(TRequest);
        var metadata = _metadataProvider.Get(requestType, request);

        // Handle cacheable queries
        if (metadata.IsCacheable)
        {
            return await HandleCacheableQueryAsync(request, next, metadata, cancellationToken).ConfigureAwait(false);
        }

        // Handle commands with invalidation
        if (metadata.InvalidatedQueryTypes.Count > 0 ||
            metadata.InvalidationPrefixes.Count > 0 ||
            _options.EnableAutomaticScopeInvalidation)
        {
            return await HandleInvalidatingCommandAsync(request, next, metadata, cancellationToken).ConfigureAwait(false);
        }

        // No caching or invalidation needed
        return await next(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> HandleCacheableQueryAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        var cacheKey = _keyFactory.CreateKey(request);

        using var _ = ThemiaLogContext.PushProperty("CacheKey", cacheKey);

        // Try to get from cache
        try
        {
            if (typeof(TResponse).IsValueType)
            {
                // A value-type default (e.g. 0/Guid.Empty/false) boxes to a non-null value, so a
                // null check can't distinguish a cached default from a miss. Probe with ExistsAsync,
                // then fetch only on a confirmed hit.
                if (await _cacheProvider.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false))
                {
                    var hitValue = await _cacheProvider.GetAsync<TResponse>(cacheKey, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Cache hit for {RequestType}", typeof(TRequest).Name);
                    return hitValue!;
                }
            }
            else
            {
                var cachedValue = await _cacheProvider.GetAsync<TResponse>(cacheKey, cancellationToken).ConfigureAwait(false);
                if (cachedValue is not null)
                {
                    _logger.LogDebug("Cache hit for {RequestType}", typeof(TRequest).Name);
                    return cachedValue;
                }
            }

            _logger.LogDebug("Cache miss for {RequestType}", typeof(TRequest).Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read from cache for {RequestType}, proceeding to handler", typeof(TRequest).Name);
        }

        // Execute handler
        TResponse response;
        try
        {
            response = await next(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Don't cache exceptions
            throw;
        }

        // Cache the response
        try
        {
            var absoluteExpiration = metadata.AbsoluteExpiration ?? _options.DefaultAbsoluteExpiration;
            var slidingExpiration = metadata.SlidingExpiration ?? _options.DefaultSlidingExpiration;

            var cacheOptions = new CacheEntryOptions
            {
                AbsoluteExpiration = absoluteExpiration,
                SlidingExpiration = slidingExpiration
            };

            await _cacheProvider.SetAsync(cacheKey, response, cacheOptions, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Cached response for {RequestType} with AbsoluteExpiration={AbsoluteExpiration}, SlidingExpiration={SlidingExpiration}",
                typeof(TRequest).Name,
                absoluteExpiration,
                slidingExpiration);

            // Track the cache key in the index
            var typePrefix = _keyFactory.CreateTypePrefix(typeof(TRequest));
            var scopeRoot = _keyFactory.CreateScopeRoot(typeof(TRequest), _options);
            var customPrefix = request is ICacheKeyProvider keyProvider ? keyProvider.GetCacheKeyPrefix() : null;

            await _keyIndex.TrackAsync(cacheKey, typeof(TRequest), typePrefix, scopeRoot, customPrefix, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to cache response for {RequestType}", typeof(TRequest).Name);
            // Don't throw - caching failures should not break the pipeline
        }

        return response;
    }

    private async Task<TResponse> HandleInvalidatingCommandAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Execute the command first
        var response = await next(cancellationToken).ConfigureAwait(false);

        // Only invalidate if command succeeded (no exception thrown)
        try
        {
            // Automatic scope invalidation
            if (_options.EnableAutomaticScopeInvalidation)
            {
                var scopeRoot = _keyFactory.CreateScopeRoot(typeof(TRequest), _options);
                if (!string.IsNullOrWhiteSpace(scopeRoot))
                {
                    _logger.LogDebug("Invalidating cache by scope root: {ScopeRoot} for {CommandType}",
                        scopeRoot, typeof(TRequest).Name);

                    await _keyIndex.RemoveByScopeRootAsync(scopeRoot, cancellationToken).ConfigureAwait(false);
                }
            }

            // Manual type-based invalidation
            foreach (var queryType in metadata.InvalidatedQueryTypes)
            {
                _logger.LogDebug("Invalidating cache for query type: {QueryType} by {CommandType}",
                    queryType.Name, typeof(TRequest).Name);

                await _keyIndex.RemoveByQueryTypeAsync(queryType, cancellationToken).ConfigureAwait(false);
            }

            // Prefix-based invalidation
            foreach (var prefix in metadata.InvalidationPrefixes)
            {
                _logger.LogDebug("Invalidating cache by prefix: {Prefix} for {CommandType}",
                    prefix, typeof(TRequest).Name);

                await _keyIndex.RemoveByPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to invalidate cache for {CommandType}", typeof(TRequest).Name);
            // Don't throw - invalidation failures should not break the pipeline
        }

        return response;
    }
}
