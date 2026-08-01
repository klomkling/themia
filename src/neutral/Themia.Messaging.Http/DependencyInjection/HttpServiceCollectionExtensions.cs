using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.Hmac;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.Http.DependencyInjection;

/// <summary>DI entry point for the HTTP outbox dispatcher.</summary>
public static class HttpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHttpClientFactory"/> and <see cref="HttpMessageDispatcher"/> as the
    /// <see cref="IOutboxDispatcher{TRow}"/> for <see cref="ClaimedMessageRow"/>. REQUIRES
    /// <c>AddThemiaMessagingHmac</c> to already be registered: <see cref="HttpMessageDispatcher"/>
    /// resolves <see cref="HmacOptions"/> to find peers, keys and routes.
    /// </summary>
    /// <remarks>
    /// Call <c>AddThemiaMessagingHmac(...)</c> BEFORE this method. This is checked by scanning the
    /// collection built so far, so calling this method before the prerequisite throws even on a host
    /// that registers it later — otherwise a host that forgets it would fail only at first dispatch
    /// with an opaque "unable to resolve service for type HmacOptions" DI activation error instead of
    /// a clear message at registration time.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><c>AddThemiaMessagingHmac</c> has not been called yet.</exception>
    public static IServiceCollection AddThemiaMessagingHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(d => d.ServiceType != typeof(HmacOptions)))
        {
            throw new InvalidOperationException(
                "AddThemiaMessagingHttp requires AddThemiaMessagingHmac(...) to already be registered: "
                + "HttpMessageDispatcher resolves HmacOptions to find peers, keys and routes. Call "
                + "AddThemiaMessagingHmac(...) BEFORE calling AddThemiaMessagingHttp.");
        }

        services.AddHttpClient();
        services.TryAddSingleton<IOutboxDispatcher<ClaimedMessageRow>, HttpMessageDispatcher>();
        return services;
    }
}
