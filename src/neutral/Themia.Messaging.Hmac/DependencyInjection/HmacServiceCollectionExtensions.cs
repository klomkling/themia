using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Messaging.Hmac.DependencyInjection;

/// <summary>DI entry point for the <c>themia-hmac-v1</c> peer registry and verifier.</summary>
public static class HmacServiceCollectionExtensions
{
    /// <summary>Configures peers and registers <see cref="HmacOptions"/> and <see cref="IHmacVerifier"/> as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Registers peers via <see cref="HmacOptions.AddPeer"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaMessagingHmac(this IServiceCollection services, Action<HmacOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HmacOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IHmacVerifier, HmacVerifier>();
        return services;
    }
}
