using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Caching;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;
using Themia.Mediator.Behaviors;
using Themia.Mediator.Configuration;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Caching;

/// <summary>
/// Exercises <see cref="CachingBehavior{TRequest,TResponse}"/> against the REAL serialization providers.
/// <para>
/// Every other test in this folder uses <c>InMemoryTestCacheProvider</c>, which stores objects directly
/// and never serializes — so nothing in the suite could observe a serializer rejecting a response type.
/// That is how coord #0100 reached a consumer: caching was silently a no-op for a plain record returned
/// through an interface, which is what the MessagePack default cannot handle.
/// </para>
/// </summary>
public sealed class CachingBehaviorSerializationTests
{
    // A plain positional record behind an interface-typed response: ordinary modern C#, and exactly
    // what MessagePack cannot serialize without a contract.
    public sealed record Facet(int Id, string? Name, int Count);

    // One request type per test: the "log once" flag is a static of the closed generic behavior type,
    // so sharing a request type between tests would let the first one consume the single warning.
    [Cacheable(AbsoluteExpirationSeconds = 300)]
    public sealed record MessagePackQuery : IRequest<IReadOnlyList<Facet>>;

    [Cacheable(AbsoluteExpirationSeconds = 300)]
    public sealed record JsonQuery : IRequest<IReadOnlyList<Facet>>;

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static (CachingBehavior<TRequest, IReadOnlyList<Facet>> Behavior,
                    CapturingLogger<CachingBehavior<TRequest, IReadOnlyList<Facet>>> Logger,
                    IThemiaCacheProvider Cache,
                    ICacheKeyFactory Keys)
        Build<TRequest>(ISerializationProvider serializer)
        where TRequest : IRequest<IReadOnlyList<Facet>>
    {
        var cache = new MemoryCacheProvider(
            new MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            serializer);
        var logger = new CapturingLogger<CachingBehavior<TRequest, IReadOnlyList<Facet>>>();
        var keys = new DefaultCacheKeyFactory();
        var index = new CacheKeyIndex(cache, new CapturingLogger<CacheKeyIndex>(), new InMemoryDistributedLockProvider());

        var behavior = new CachingBehavior<TRequest, IReadOnlyList<Facet>>(
            logger,
            cache,
            keys,
            new AttributeCacheMetadataProvider(),
            index,
            Options.Create(new MediatorCachingOptions()));

        return (behavior, logger, cache, keys);
    }

    private static Task<IReadOnlyList<Facet>> Handler(CancellationToken _)
        => Task.FromResult<IReadOnlyList<Facet>>([new Facet(1, "a", 2)]);

    [Fact]
    public async Task Should_warn_that_the_response_will_never_be_cached_when_the_serializer_rejects_it()
    {
        var (behavior, logger, cache, keys) = Build<MessagePackQuery>(new MessagePackSerializationProvider());
        var request = new MessagePackQuery();

        var first = await behavior.HandleAsync(request, Handler, CancellationToken.None);
        var second = await behavior.HandleAsync(request, Handler, CancellationToken.None);

        // The request still succeeds - a cache fault must never break the handler.
        Assert.Single(first);
        Assert.Single(second);

        // ...but nothing was stored, on this request or any other.
        Assert.False(await cache.ExistsAsync(keys.CreateKey(request), CancellationToken.None));

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();

        // Logged once per request type, not once per request: a hot path must not flood the log.
        var warning = Assert.Single(warnings);

        // "will never be stored" is the load-bearing part. "Failed to cache" reads as transient and
        // gets ignored; this failure repeats identically forever until the configuration changes.
        Assert.Contains("NEVER be stored", warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(MessagePackQuery), warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(MessagePackSerializationProvider), warning.Message, StringComparison.Ordinal);

        var serializationException = Assert.IsType<CacheSerializationException>(warning.Exception);
        Assert.Equal(typeof(IReadOnlyList<Facet>), serializationException.SerializedType);
    }

    [Fact]
    public async Task Should_cache_the_same_response_shape_when_the_serializer_can_handle_it()
    {
        var (behavior, logger, cache, keys) = Build<JsonQuery>(new JsonSerializationProvider());
        var request = new JsonQuery();
        var handlerCalls = 0;

        Task<IReadOnlyList<Facet>> CountingHandler(CancellationToken ct)
        {
            handlerCalls++;
            return Handler(ct);
        }

        await behavior.HandleAsync(request, CountingHandler, CancellationToken.None);
        await behavior.HandleAsync(request, CountingHandler, CancellationToken.None);

        // Same request, same response type: the only thing that changed is the serializer.
        Assert.Equal(1, handlerCalls);
        Assert.True(await cache.ExistsAsync(keys.CreateKey(request), CancellationToken.None));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
