using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.DependencyInjection;

namespace Themia.Messaging.Hmac.DependencyInjection;

/// <summary>DI entry point for the <c>themia-hmac-v1</c> peer registry and verifier.</summary>
public static class HmacServiceCollectionExtensions
{
    /// <summary>Configures peers and registers <see cref="HmacOptions"/> and <see cref="IHmacVerifier"/> as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Registers peers via <see cref="HmacOptions.AddPeer"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="HmacOptions"/> is already registered. <c>TryAddSingleton</c> would silently discard the
    /// second call's <paramref name="configure"/> — and every peer it registered — because the type is
    /// already present; in a modular host where two modules each call this method, the second module's
    /// peers would vanish and surface later as 401s on receive or Permanent dead-letters on send.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingHmac(this IServiceCollection services, Action<HmacOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        MessagingRegistrationGuards.ThrowIfAlreadyRegistered<HmacOptions>(
            services,
            "AddThemiaMessagingHmac has already been called: HmacOptions is already registered. Calling "
            + "it again would silently discard this call's peers instead of adding to the existing "
            + "registry. Register all peers in a single AddThemiaMessagingHmac(...) call.");

        var options = new HmacOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IHmacVerifier, HmacVerifier>();
        return services;
    }
}
