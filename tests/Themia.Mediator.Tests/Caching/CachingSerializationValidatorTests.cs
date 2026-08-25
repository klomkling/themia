using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Caching;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Attributes;
using Themia.Caching.Extensions;
using Themia.Mediator.Extensions;

namespace Themia.Mediator.Tests.Caching;

/// <summary>
/// The opt-in startup check from coord #0100: a [Cacheable] request whose response the configured
/// serializer cannot handle is reported at startup, by name, instead of on the first request that
/// happens to reach it.
/// </summary>
public sealed class CachingSerializationValidatorTests
{
    public sealed record Facet(int Id, string? Name, int Count);

    [Cacheable(AbsoluteExpirationSeconds = 300)]
    public sealed record UnserializableQuery : IRequest<IReadOnlyList<Facet>>;

    [Cacheable(AbsoluteExpirationSeconds = 300)]
    public sealed record FineQuery : IRequest<string>;

    public sealed record NotCachedQuery : IRequest<IReadOnlyList<Facet>>;

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Entries);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static async Task<List<(LogLevel Level, string Message)>> RunHostAsync(
        Action<IServiceCollection> configure)
    {
        var captured = new CapturingProvider();

        var host = new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(captured);
            })
            .ConfigureServices(configure)
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        return captured.Entries;
    }

    [Fact]
    public async Task Should_name_the_request_whose_response_the_serializer_cannot_handle()
    {
        var entries = await RunHostAsync(services =>
        {
            services.AddThemiaCaching();  // MessagePack default
            services.AddSingleton<IRequestHandler<UnserializableQuery, IReadOnlyList<Facet>>,
                UnserializableQueryHandler>();
            services.AddSingleton<IRequestHandler<FineQuery, string>, FineQueryHandler>();
            services.ValidateThemiaCachingSerialization();
        });

        var errors = entries.Where(e => e.Level == LogLevel.Error).ToList();
        var error = Assert.Single(errors);

        Assert.Contains(nameof(UnserializableQuery), error.Message, StringComparison.Ordinal);
        // One message listing everything, not one per type.
        Assert.DoesNotContain(nameof(FineQuery), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_stay_silent_when_every_cacheable_response_is_serializable()
    {
        var entries = await RunHostAsync(services =>
        {
            services.AddThemiaCaching(c => c.UseMemoryCache().UseJsonSerialization());
            services.AddSingleton<IRequestHandler<UnserializableQuery, IReadOnlyList<Facet>>,
                UnserializableQueryHandler>();
            services.ValidateThemiaCachingSerialization();
        });

        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Should_ignore_a_request_that_is_not_cacheable()
    {
        var entries = await RunHostAsync(services =>
        {
            services.AddThemiaCaching();  // MessagePack default
            services.AddSingleton<IRequestHandler<NotCachedQuery, IReadOnlyList<Facet>>,
                NotCachedQueryHandler>();
            services.ValidateThemiaCachingSerialization();
        });

        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Should_work_when_called_before_the_handlers_are_registered()
    {
        // The ordering trap coord #0100 reported for AddThemiaCaching must not be reintroduced here:
        // the validator reads the service collection when the host starts, not when it is registered.
        var entries = await RunHostAsync(services =>
        {
            services.ValidateThemiaCachingSerialization();
            services.AddThemiaCaching();  // MessagePack default
            services.AddSingleton<IRequestHandler<UnserializableQuery, IReadOnlyList<Facet>>,
                UnserializableQueryHandler>();
        });

        var error = Assert.Single(entries, e => e.Level == LogLevel.Error);
        Assert.Contains(nameof(UnserializableQuery), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_not_stop_the_host()
    {
        // Opt-in diagnostic, not a boot gate: a deployment that is running today keeps running.
        var entries = await RunHostAsync(services =>
        {
            services.AddThemiaCaching();
            services.AddSingleton<IRequestHandler<UnserializableQuery, IReadOnlyList<Facet>>,
                UnserializableQueryHandler>();
            services.ValidateThemiaCachingSerialization();
        });

        // Reaching here at all means StartAsync did not throw.
        Assert.NotEmpty(entries);
    }
}

/// <summary>
/// Stand-in handlers. Closed, top-level and internal: THEMIA013 refuses a private nested handler and
/// THEMIA012 refuses an open generic one, so each request gets its own concrete handler.
/// </summary>
internal sealed class UnserializableQueryHandler
    : IRequestHandler<CachingSerializationValidatorTests.UnserializableQuery, IReadOnlyList<CachingSerializationValidatorTests.Facet>>
{
    public Task<IReadOnlyList<CachingSerializationValidatorTests.Facet>> HandleAsync(
        CachingSerializationValidatorTests.UnserializableQuery request,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CachingSerializationValidatorTests.Facet>>([]);
}

internal sealed class FineQueryHandler : IRequestHandler<CachingSerializationValidatorTests.FineQuery, string>
{
    public Task<string> HandleAsync(
        CachingSerializationValidatorTests.FineQuery request,
        CancellationToken cancellationToken)
        => Task.FromResult(string.Empty);
}

internal sealed class NotCachedQueryHandler
    : IRequestHandler<CachingSerializationValidatorTests.NotCachedQuery, IReadOnlyList<CachingSerializationValidatorTests.Facet>>
{
    public Task<IReadOnlyList<CachingSerializationValidatorTests.Facet>> HandleAsync(
        CachingSerializationValidatorTests.NotCachedQuery request,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CachingSerializationValidatorTests.Facet>>([]);
}
