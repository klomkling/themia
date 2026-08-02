using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging;
using Themia.Messaging.DependencyInjection;
using Themia.Messaging.Hmac;

namespace Themia.Messaging.AspNetCore.DependencyInjection;

/// <summary>DI entry point for the <c>themia-hmac-v1</c> inbound verification filter.</summary>
public static class AspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="VerificationOptions"/> and the services <see cref="HmacVerificationFilter"/>
    /// needs, and schedules the loop-guard startup warning. REQUIRES <c>AddThemiaMessagingHmac</c> and
    /// <c>AddThemiaMessagingIdentity</c> to already be registered: <see cref="HmacVerificationFilter"/>
    /// resolves <see cref="HmacOptions"/> to find the peer a route was protected with, and compares this
    /// service's <see cref="MessagingIdentity"/> against the inbound Origin header to detect a loop.
    /// </summary>
    /// <remarks>
    /// Call <c>AddThemiaMessagingHmac(...)</c> and <c>AddThemiaMessagingIdentity(...)</c> BEFORE this
    /// method. Both are checked by scanning the collection built so far, so calling this method before
    /// either prerequisite throws even on a host that registers it later — otherwise a host that forgets
    /// one would fail only at first request with an opaque endpoint-filter activation error instead of a
    /// clear message at registration time.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares bi-directional peers.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>AddThemiaMessagingHmac</c> or <c>AddThemiaMessagingIdentity</c> has not been called yet.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingVerification(
        this IServiceCollection services, Action<VerificationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        MessagingRegistrationGuards.RequireRegistered<HmacOptions>(
            services,
            "AddThemiaMessagingVerification requires AddThemiaMessagingHmac(...) to already be "
            + "registered: HmacVerificationFilter resolves HmacOptions to find the peer a route was "
            + "protected with. Call AddThemiaMessagingHmac(...) BEFORE calling AddThemiaMessagingVerification.");

        MessagingRegistrationGuards.RequireRegistered<MessagingIdentity>(
            services,
            "AddThemiaMessagingVerification requires AddThemiaMessagingIdentity(...) to already be "
            + "registered: HmacVerificationFilter compares this service's identity against the inbound "
            + "Origin header to detect a message that has looped back. Call AddThemiaMessagingIdentity(...) "
            + "BEFORE calling AddThemiaMessagingVerification.");

        var options = new VerificationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<LoopGuardStartupWarnings>();

        return services;
    }
}

/// <summary>Attaches <see cref="HmacVerificationFilter"/> to a minimal-API route.</summary>
public static class RouteHandlerBuilderExtensions
{
    /// <summary>
    /// Requires an inbound <c>themia-hmac-v1</c> signature from <paramref name="peerName"/> before this
    /// route's handler runs. The peer must already be registered via <c>HmacOptions.AddPeer</c> — its
    /// keys, header prefix, clock tolerance and body size limit all come from that registration, so the
    /// name has to be known up front rather than discovered from the request.
    /// </summary>
    /// <param name="builder">The route to protect.</param>
    /// <param name="peerName">The peer's name, as registered via <c>HmacOptions.AddPeer</c>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="peerName"/> is null or empty.</exception>
    public static RouteHandlerBuilder RequireThemiaHmac(this RouteHandlerBuilder builder, string peerName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(peerName);

        return builder
            .WithMetadata(new ThemiaHmacPeerMetadata(peerName))
            .AddEndpointFilter<HmacVerificationFilter>();
    }
}

/// <summary>Endpoint metadata carrying the peer name a route was protected with, read by <see cref="HmacVerificationFilter"/>.</summary>
/// <param name="PeerName">The peer's name.</param>
internal sealed record ThemiaHmacPeerMetadata(string PeerName);
