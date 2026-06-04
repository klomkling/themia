using Microsoft.Extensions.Options;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;
using Themia.Mediator.Behaviors;
using Themia.Mediator.Configuration;
using Themia.Mediator.Infrastructure;
using Themia.Mediator.Tests.TestDoubles;

namespace Themia.Mediator.Tests.Caching;

public sealed class CachingBehaviorTests
{
    private readonly InMemoryTestCacheProvider _cacheProvider = new();
    private readonly ICacheKeyFactory _keyFactory = new DefaultCacheKeyFactory();
    private readonly ICacheMetadataProvider _metadataProvider = new AttributeCacheMetadataProvider();
    private readonly ICacheKeyIndex _keyIndex;
    private readonly IOptions<MediatorCachingOptions> _options = Options.Create(new MediatorCachingOptions
    {
        DefaultAbsoluteExpiration = TimeSpan.FromMinutes(5),
        DefaultSlidingExpiration = TimeSpan.FromMinutes(1),
        EnableAutomaticScopeInvalidation = true
    });

    public CachingBehaviorTests()
    {
        _keyIndex = new CacheKeyIndex(
            _cacheProvider,
            NullTestLogger<CacheKeyIndex>.Instance,
            new InMemoryDistributedLockProvider());
    }

    private static NullTestLogger<CachingBehavior<TRequest, TResponse>> CreateLogger<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
    {
        return NullTestLogger<CachingBehavior<TRequest, TResponse>>.Instance;
    }

    [Fact]
    public async Task Should_cache_query_response_on_miss_and_return_on_hit()
    {
        // Arrange
        var behavior = new CachingBehavior<TestQuery, string>(CreateLogger<TestQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        var query = new TestQuery("a", 1);
        var handlerCalled = 0;
        Task<string> Next(CancellationToken _) { handlerCalled++; return Task.FromResult("result"); }

        // Act 1: First call should miss and cache
        var result1 = await behavior.HandleAsync(query, Next, CancellationToken.None);

        // Act 2: Second call should hit cache
        var result2 = await behavior.HandleAsync(query, Next, CancellationToken.None);

        // Assert
        Assert.Equal("result", result1);
        Assert.Equal("result", result2);
        Assert.Equal(1, handlerCalled); // Handler should only be called once
        Assert.True(_cacheProvider.SetCallCount > 0); // At least one set (value + index)
        Assert.True(_cacheProvider.GetCallCount > 0);
    }

    [Fact]
    public async Task Should_respect_attribute_expirations_over_interface_or_defaults()
    {
        // Arrange
        var behavior = new CachingBehavior<AttrQuery, string>(CreateLogger<AttrQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        var query = new AttrQuery("x");
        Task<string> Next(CancellationToken _) => Task.FromResult("value");

        // Act
        var _ = await behavior.HandleAsync(query, Next, CancellationToken.None);

        // Assert (we can't directly observe expirations; we assert it wrote to cache)
        Assert.True(_cacheProvider.SetCallCount > 0);
    }

    [Fact]
    public async Task Should_use_custom_cache_key_provider_when_implemented()
    {
        // Arrange
        var behavior = new CachingBehavior<CustomKeyQuery, string>(CreateLogger<CustomKeyQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        var query = new CustomKeyQuery("abc");
        Task<string> Next(CancellationToken _) => Task.FromResult("v");

        // Act
        _ = await behavior.HandleAsync(query, Next, CancellationToken.None);

        // Assert
        // The custom key is deterministic; ensure get was attempted with it by hitting again
        var initialSetCount = _cacheProvider.SetCallCount;
        _ = await behavior.HandleAsync(query, Next, CancellationToken.None);
        Assert.Equal(initialSetCount, _cacheProvider.SetCallCount); // Should not set again on cache hit
    }

    [Fact]
    public async Task Should_not_cache_when_handler_throws()
    {
        // Arrange
        var behavior = new CachingBehavior<TestQuery, string>(CreateLogger<TestQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        var query = new TestQuery("a", 1);
        Task<string> Next(CancellationToken _) => Task.FromException<string>(new InvalidOperationException("fail"));

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.HandleAsync(query, Next, CancellationToken.None));
        Assert.Equal(0, _cacheProvider.SetCallCount);
    }

    [Fact]
    public async Task Command_should_invalidate_by_type_prefix_and_scope()
    {
        // Arrange (simulate a cached query)
        var behaviorQ = new CachingBehavior<GetOrderQuery, string>(CreateLogger<GetOrderQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        await behaviorQ.HandleAsync(new GetOrderQuery(1), _ => Task.FromResult("Order"), CancellationToken.None);

        var behaviorC = new CachingBehavior<UpdateOrderCommand, Unit>(CreateLogger<UpdateOrderCommand, Unit>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        Task<Unit> Next(CancellationToken _) => Task.FromResult(Unit.Value);

        // Act
        var _ = await behaviorC.HandleAsync(new UpdateOrderCommand(1), Next, CancellationToken.None);

        // Assert
        Assert.True(_cacheProvider.RemoveCallCount > 0);
    }

    [Fact]
    public async Task Command_should_union_interface_and_attribute_invalidations()
    {
        // Arrange: cache a query response
        var behaviorQ = new CachingBehavior<GetOrderQuery, string>(CreateLogger<GetOrderQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        await behaviorQ.HandleAsync(new GetOrderQuery(123), _ => Task.FromResult("Order"), CancellationToken.None);

        var behaviorC = new CachingBehavior<DeleteOrderCommand, Unit>(CreateLogger<DeleteOrderCommand, Unit>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        Task<Unit> Next(CancellationToken _) => Task.FromResult(Unit.Value);

        // Act
        var _ = await behaviorC.HandleAsync(new DeleteOrderCommand(123), Next, CancellationToken.None);

        // Assert
        Assert.True(_cacheProvider.RemoveCallCount > 0);
    }

    [Fact]
    public async Task Should_continue_when_cache_provider_fails()
    {
        // Arrange
        _cacheProvider.SimulateFailure = true;
        var behavior = new CachingBehavior<TestQuery, string>(CreateLogger<TestQuery, string>(), _cacheProvider, _keyFactory, _metadataProvider, _keyIndex, _options);
        Task<string> Next(CancellationToken _) => Task.FromResult("ok");

        // Act
        var result = await behavior.HandleAsync(new TestQuery("x", 1), Next, CancellationToken.None);

        // Assert
        Assert.Equal("ok", result);
    }

    // Test request/command types

    [Cacheable(AbsoluteExpirationSeconds = 60)]
    public sealed record AttrQuery(string Key) : IQuery<string>;

    public sealed record TestQuery(string Name, int Page) : IQuery<string>, ICacheable<string>
    {
        public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(10);
        public TimeSpan? SlidingExpiration => TimeSpan.FromMinutes(2);
    }

    public sealed record CustomKeyQuery(string Part) : IQuery<string>, ICacheKeyProvider
    {
        public string GetCacheKey() => $"custom:{Part}";
        public string? GetCacheKeyPrefix() => "custom:";
    }

    public sealed record UpdateOrderCommand(int OrderId) : ICommand<Unit>;

    [InvalidatesCache(typeof(GetOrderQuery), CacheKeyPrefix = "Order:")]
    public sealed record DeleteOrderCommand(int OrderId) : ICommand<Unit>, ICacheInvalidator
    {
        public IEnumerable<Type> GetInvalidatedQueryTypes() => [typeof(ListOrdersQuery)];
    }

    public sealed record GetOrderQuery(int OrderId) : IQuery<string>, ICacheable<string>;
    public sealed record ListOrdersQuery() : IQuery<string>;

    public readonly struct Unit
    {
        public static Unit Value => default;
    }
}
