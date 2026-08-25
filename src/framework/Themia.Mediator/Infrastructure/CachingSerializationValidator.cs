using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Caching;
using Themia.Mediator.Abstractions;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Reports, once at startup, every <c>[Cacheable]</c> request whose response type the configured
/// <see cref="ISerializationProvider"/> cannot serialize.
/// </summary>
/// <remarks>
/// Such a request is not broken — it answers correctly — but its response is never stored, on any
/// request, for the lifetime of the process. Without this check that is only discoverable when the
/// first request of that particular query happens to run, which for a rarely-hit query can be a long
/// time after the deploy that introduced it (coord #0100).
/// <para>
/// This <b>never stops the host</b>. It is a diagnostic a consumer opts into, and a deployment that
/// runs today keeps running.
/// </para>
/// </remarks>
internal sealed class CachingSerializationValidator : IHostedService
{
    private readonly IServiceCollection _services;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<CachingSerializationValidator> _logger;

    public CachingSerializationValidator(
        IServiceCollection services,
        IServiceProvider rootProvider,
        ILogger<CachingSerializationValidator> logger)
    {
        _services = services;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Read the descriptors at START, not at registration: the collection is only complete once the
        // host is built, so this works whether the consumer calls the registration before or after
        // their handlers. Reading it eagerly would reintroduce exactly the ordering trap #0100 reported.
        using var scope = _rootProvider.CreateScope();
        var provider = scope.ServiceProvider;

        var serializer = provider.GetService<ISerializationProvider>();
        if (serializer is null)
        {
            // Nothing configured to validate against.
            return Task.CompletedTask;
        }

        var metadata = provider.GetService<ICacheMetadataProvider>() ?? new AttributeCacheMetadataProvider();

        var unserializable = new List<string>();

        foreach (var descriptor in _services)
        {
            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType ||
                serviceType.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
            {
                continue;
            }

            var arguments = serviceType.GetGenericArguments();
            var requestType = arguments[0];
            var responseType = arguments[1];

            if (!metadata.Get(requestType, null).IsCacheable)
            {
                continue;
            }

            if (!serializer.CanHandle(responseType))
            {
                unserializable.Add($"{requestType.Name} -> {responseType.FullName}");
            }
        }

        if (unserializable.Count > 0)
        {
            // One message listing everything: an operator needs the set, not one line per type.
            _logger.LogError(
                "Caching is DISABLED for {Count} cacheable request(s): the configured serializer "
                + "({SerializerName}) cannot serialize their response types, so their responses will "
                + "NEVER be stored. Affected: {Affected}.",
                unserializable.Count,
                serializer.GetType().Name,
                string.Join("; ", unserializable));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
