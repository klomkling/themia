using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Caching;

public sealed class AttributeCacheMetadataProviderTests
{
    private readonly AttributeCacheMetadataProvider _provider = new();

    [Fact]
    public void Should_detect_cacheable_via_interface()
    {
        // Arrange
        var request = new InterfaceCacheableQuery();

        // Act
        var metadata = _provider.Get(typeof(InterfaceCacheableQuery), request);

        // Assert
        Assert.True(metadata.IsCacheable);
        Assert.Equal(TimeSpan.FromMinutes(5), metadata.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromMinutes(1), metadata.SlidingExpiration);
    }

    [Fact]
    public void Should_detect_cacheable_via_attribute()
    {
        // Arrange
        var request = new AttributeCacheableQuery();

        // Act
        var metadata = _provider.Get(typeof(AttributeCacheableQuery), request);

        // Assert
        Assert.True(metadata.IsCacheable);
        Assert.Equal(TimeSpan.FromSeconds(120), metadata.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromSeconds(30), metadata.SlidingExpiration);
    }

    [Fact]
    public void Should_prefer_attribute_over_interface_for_expiration()
    {
        // Arrange
        var request = new BothCacheableQuery();

        // Act
        var metadata = _provider.Get(typeof(BothCacheableQuery), request);

        // Assert
        Assert.True(metadata.IsCacheable);
        Assert.Equal(TimeSpan.FromSeconds(200), metadata.AbsoluteExpiration); // from attribute
        Assert.Equal(TimeSpan.FromSeconds(50), metadata.SlidingExpiration);   // from attribute
    }

    [Fact]
    public void Should_detect_not_cacheable()
    {
        // Arrange
        var request = new NotCacheableQuery();

        // Act
        var metadata = _provider.Get(typeof(NotCacheableQuery), request);

        // Assert
        Assert.False(metadata.IsCacheable);
    }

    [Fact]
    public void Should_detect_invalidation_via_interface()
    {
        // Arrange
        var command = new InterfaceInvalidatorCommand();

        // Act
        var metadata = _provider.Get(typeof(InterfaceInvalidatorCommand), command);

        // Assert
        Assert.Contains(typeof(SomeQuery), metadata.InvalidatedQueryTypes);
    }

    [Fact]
    public void Should_detect_invalidation_via_attribute()
    {
        // Arrange
        var command = new AttributeInvalidatorCommand();

        // Act
        var metadata = _provider.Get(typeof(AttributeInvalidatorCommand), null);

        // Assert
        Assert.Contains(typeof(SomeQuery), metadata.InvalidatedQueryTypes);
        Assert.Contains("Order:", metadata.InvalidationPrefixes);
    }

    [Fact]
    public void Should_union_interface_and_attribute_invalidations()
    {
        // Arrange
        var command = new BothInvalidatorCommand();

        // Act
        var metadata = _provider.Get(typeof(BothInvalidatorCommand), command);

        // Assert
        Assert.Contains(typeof(SomeQuery), metadata.InvalidatedQueryTypes);
        Assert.Contains(typeof(AnotherQuery), metadata.InvalidatedQueryTypes);
        Assert.Contains("User:", metadata.InvalidationPrefixes);
    }

    [Fact]
    public void Should_cache_attribute_metadata_per_type()
    {
        // Act: Call twice
        var metadata1 = _provider.Get(typeof(AttributeCacheableQuery), null);
        var metadata2 = _provider.Get(typeof(AttributeCacheableQuery), null);

        // Assert: Should return same metadata (cached)
        Assert.Equal(metadata1.IsCacheable, metadata2.IsCacheable);
        Assert.Equal(metadata1.AbsoluteExpiration, metadata2.AbsoluteExpiration);
    }

    // Test types
    public sealed record InterfaceCacheableQuery : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(5);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(1);
    }

    [Cacheable(AbsoluteExpirationSeconds = 120, SlidingExpirationSeconds = 30)]
    public sealed record AttributeCacheableQuery : IQuery<string>;

    [Cacheable(AbsoluteExpirationSeconds = 200, SlidingExpirationSeconds = 50)]
    public sealed record BothCacheableQuery : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(10);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(2);
    }

    public sealed record NotCacheableQuery : IQuery<string>;

    public sealed record InterfaceInvalidatorCommand : ICommand<bool>, ICacheInvalidator
    {
        public IEnumerable<Type> GetInvalidatedQueryTypes() => [typeof(SomeQuery)];
    }

    [InvalidatesCache(typeof(SomeQuery), CacheKeyPrefix = "Order:")]
    public sealed record AttributeInvalidatorCommand : ICommand<bool>;

    [InvalidatesCache(typeof(AnotherQuery), CacheKeyPrefix = "User:")]
    public sealed record BothInvalidatorCommand : ICommand<bool>, ICacheInvalidator
    {
        public IEnumerable<Type> GetInvalidatedQueryTypes() => [typeof(SomeQuery)];
    }

    public sealed record SomeQuery : IQuery<string>;
    public sealed record AnotherQuery : IQuery<string>;
}
