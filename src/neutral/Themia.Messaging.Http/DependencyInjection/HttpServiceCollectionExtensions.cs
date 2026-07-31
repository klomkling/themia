using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.Outbox;

namespace Themia.Messaging.Http.DependencyInjection;

/// <summary>DI entry point for the HTTP outbox dispatcher.</summary>
public static class HttpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHttpClientFactory"/> and <see cref="HttpMessageDispatcher"/> as the
    /// <see cref="IOutboxDispatcher{TRow}"/> for <see cref="ClaimedMessageRow"/>. Requires
    /// <c>AddThemiaMessagingHmac</c> to have registered <c>HmacOptions</c> — the dispatcher resolves
    /// peers from it at delivery time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaMessagingHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        services.TryAddSingleton<IOutboxDispatcher<ClaimedMessageRow>, HttpMessageDispatcher>();
        return services;
    }
}
