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

        var hmacOptions = FindRegisteredHmacOptions(services);
        if (hmacOptions is null)
        {
            throw new InvalidOperationException(
                "AddThemiaMessagingHttp requires AddThemiaMessagingHmac(...) to already be registered: "
                + "HttpMessageDispatcher resolves HmacOptions to find peers, keys and routes. Call "
                + "AddThemiaMessagingHmac(...) BEFORE calling AddThemiaMessagingHttp.");
        }

        services.AddHttpClient();

        // A redirect must never be followed automatically on a peer's client: a 301/302/303 silently
        // converts the signed POST to a GET and drops the body (the receiver 401s and the channel
        // dead-letters looking like a key problem), while a 307/308 would replay the signed payload,
        // verbatim and validly signed, to whatever host Location names. HttpStatusClassifier already
        // treats 3xx as Permanent — this just lets it see the 3xx at all.
        //
        // This is configured per PEER NAME (via AddHttpClient(peerName), read off the HmacOptions instance
        // already registered by AddThemiaMessagingHmac) rather than via ConfigureHttpClientDefaults, which
        // would apply to EVERY HttpClient the factory produces for the whole host — including OIDC
        // discovery/authorization in Themia.Modules.Identity.ExternalAuth.AspNetCore, which depends on
        // following redirects, plus any other IHttpClientFactory consumer (SMS providers, integration
        // services) that never opted into this module at all.
        foreach (var peerName in hmacOptions.PeerNames)
        {
            services.AddHttpClient(peerName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        }

        services.TryAddSingleton<IOutboxDispatcher<ClaimedMessageRow>, HttpMessageDispatcher>();
        return services;
    }

    // Mirrors Themia.Modules.Messaging.DependencyInjection.MessagingServiceCollectionExtensions'
    // ContributeDapperMappings: reads a registered singleton instance out of the collection at
    // registration time, without building a provider (a provider can't be built mid-registration, and
    // building one just to read one value would be wasteful besides).
    private static HmacOptions? FindRegisteredHmacOptions(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(HmacOptions) && services[i].ImplementationInstance is HmacOptions options)
            {
                return options;
            }
        }

        return null;
    }
}
