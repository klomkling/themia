using System.Collections.Concurrent;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Provides cache metadata by reading attributes and interfaces.
/// Caches attribute-based metadata per type to minimize reflection overhead.
/// </summary>
public sealed class AttributeCacheMetadataProvider : ICacheMetadataProvider
{
    private readonly ConcurrentDictionary<Type, AttributeMetadata> _attributeCache = new();

    /// <inheritdoc />
    public CacheMetadata Get(Type requestType, object? requestInstance)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        // Get cached attribute metadata (only uses reflection once per type)
        var attrMetadata = _attributeCache.GetOrAdd(requestType, ReadAttributeMetadata);

        var cacheableInterface = GetCacheableInterface(requestType);
        var isCacheable = attrMetadata.HasCacheableAttribute || cacheableInterface is not null;

        // Collect invalidation info from both attribute and interface (regardless of cacheability)
        var invalidatedTypes = new HashSet<Type>(attrMetadata.InvalidatedQueryTypes);
        if (requestInstance is ICacheInvalidator invalidator)
        {
            foreach (var type in invalidator.GetInvalidatedQueryTypes())
            {
                invalidatedTypes.Add(type);
            }
        }

        if (!isCacheable)
        {
            // Not cacheable, but might have invalidation metadata
            if (invalidatedTypes.Count > 0 || attrMetadata.InvalidationPrefixes.Count > 0)
            {
                return new CacheMetadata
                {
                    IsCacheable = false,
                    InvalidatedQueryTypes = invalidatedTypes,
                    InvalidationPrefixes = attrMetadata.InvalidationPrefixes
                };
            }

            return CacheMetadata.None;
        }

        // Resolve expirations: Attribute > Interface > null
        TimeSpan? absoluteExpiration = attrMetadata.AbsoluteExpiration;
        TimeSpan? slidingExpiration = attrMetadata.SlidingExpiration;

        // If attribute didn't provide values, try reading from interface properties
        if (requestInstance is not null &&
            cacheableInterface is not null &&
            (absoluteExpiration is null || slidingExpiration is null))
        {
            // Get properties via reflection (minimal usage)
            var absExpirationProp = cacheableInterface.GetProperty("AbsoluteExpiration");
            var slidingExpirationProp = cacheableInterface.GetProperty("SlidingExpiration");

            if (absoluteExpiration is null)
            {
                absoluteExpiration = absExpirationProp?.GetValue(requestInstance) as TimeSpan?;
            }

            if (slidingExpiration is null)
            {
                slidingExpiration = slidingExpirationProp?.GetValue(requestInstance) as TimeSpan?;
            }
        }

        return new CacheMetadata
        {
            IsCacheable = true,
            AbsoluteExpiration = absoluteExpiration,
            SlidingExpiration = slidingExpiration,
            InvalidatedQueryTypes = invalidatedTypes,
            InvalidationPrefixes = attrMetadata.InvalidationPrefixes
        };
    }

    private static AttributeMetadata ReadAttributeMetadata(Type type)
    {
        var metadata = new AttributeMetadata();

        // Read CacheableAttribute
        var cacheableAttr = Attribute.GetCustomAttribute(type, typeof(CacheableAttribute)) as CacheableAttribute;
        if (cacheableAttr is not null)
        {
            metadata.HasCacheableAttribute = true;

            if (cacheableAttr.AbsoluteExpirationSeconds > 0)
            {
                metadata.AbsoluteExpiration = TimeSpan.FromSeconds(cacheableAttr.AbsoluteExpirationSeconds);
            }

            if (cacheableAttr.SlidingExpirationSeconds > 0)
            {
                metadata.SlidingExpiration = TimeSpan.FromSeconds(cacheableAttr.SlidingExpirationSeconds);
            }
        }

        // Read InvalidatesCacheAttribute
        var invalidatesAttr = Attribute.GetCustomAttribute(type, typeof(InvalidatesCacheAttribute)) as InvalidatesCacheAttribute;
        if (invalidatesAttr is not null)
        {
            metadata.InvalidatedQueryTypes = new HashSet<Type>(invalidatesAttr.QueryTypes);

            if (!string.IsNullOrWhiteSpace(invalidatesAttr.CacheKeyPrefix))
            {
                metadata.InvalidationPrefixes = new HashSet<string> { invalidatesAttr.CacheKeyPrefix };
            }
        }

        return metadata;
    }

    private sealed class AttributeMetadata
    {
        public bool HasCacheableAttribute { get; set; }
        public TimeSpan? AbsoluteExpiration { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public IReadOnlySet<Type> InvalidatedQueryTypes { get; set; } = new HashSet<Type>();
        public IReadOnlySet<string> InvalidationPrefixes { get; set; } = new HashSet<string>();
    }

    private static Type? GetCacheableInterface(Type type) =>
        type
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICacheable<>));
}
