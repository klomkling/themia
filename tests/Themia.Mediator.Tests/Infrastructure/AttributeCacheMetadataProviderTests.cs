using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Infrastructure;

public sealed class AttributeCacheMetadataProviderTests
{
    private readonly AttributeCacheMetadataProvider _provider = new();

    [Fact]
    public void Should_detect_interface_based_cacheable()
    {
        var request = new InterfaceOnlyQuery();

        var metadata = _provider.Get(request.GetType(), request);

        Assert.True(metadata.IsCacheable);
        Assert.Equal(TimeSpan.FromMinutes(5), metadata.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromMinutes(1), metadata.SlidingExpiration);
    }

    [Fact]
    public void Attribute_should_override_absolute_only()
    {
        var request = new AttributeOverridesAbsolute();

        var metadata = _provider.Get(request.GetType(), request);

        Assert.Equal(TimeSpan.FromSeconds(60), metadata.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromMinutes(2), metadata.SlidingExpiration);
    }

    [Fact]
    public void Attribute_should_override_sliding_only()
    {
        var request = new AttributeOverridesSliding();

        var metadata = _provider.Get(request.GetType(), request);

        Assert.Equal(TimeSpan.FromMinutes(3), metadata.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromSeconds(30), metadata.SlidingExpiration);
    }

    private sealed record InterfaceOnlyQuery() : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(5);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(1);
    }

    [Cacheable(AbsoluteExpirationSeconds = 60)]
    private sealed record AttributeOverridesAbsolute() : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(2);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(2);
    }

    [Cacheable(SlidingExpirationSeconds = 30)]
    private sealed record AttributeOverridesSliding() : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(3);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(1);
    }
}
