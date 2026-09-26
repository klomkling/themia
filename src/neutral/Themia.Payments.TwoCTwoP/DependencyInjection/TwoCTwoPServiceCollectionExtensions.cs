using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Themia.Payments.DependencyInjection;

namespace Themia.Payments.TwoCTwoP.DependencyInjection;

/// <summary>DI entry point for the 2C2P adapter.</summary>
public static class TwoCTwoPServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TwoCTwoPOptions"/> (validated with <c>ValidateOnStart</c>), the named
    /// <see cref="HttpClient"/>, and <see cref="TwoCTwoPPaymentGateway"/> as <see cref="IPaymentGateway"/> and
    /// <see cref="IPaymentGatewayCapabilities"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the merchant id, secret key and environment.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaPaymentsTwoCTwoP(
        this IServiceCollection services, Action<TwoCTwoPOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<TwoCTwoPOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.MerchantId), "TwoCTwoPOptions.MerchantId must be set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SecretKey), "TwoCTwoPOptions.SecretKey must be set.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "TwoCTwoPOptions.Timeout must be positive.")
            .ValidateOnStart();

        services.AddHttpClient(TwoCTwoPPaymentGateway.HttpClientName, (sp, client) =>
        {
            var twoCTwoPOptions = sp.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value;
            client.BaseAddress = TwoCTwoPOptions.BaseAddressFor(twoCTwoPOptions.Environment);
            client.Timeout = twoCTwoPOptions.Timeout;
        });

        services.TryAddSingleton<TwoCTwoPPaymentGateway>();
        services.TryAddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<TwoCTwoPPaymentGateway>());
        services.TryAddSingleton<IPaymentGatewayCapabilities>(sp => sp.GetRequiredService<TwoCTwoPPaymentGateway>());

        // So the gate exists even when the host forgot the core call. AddThemiaPayments is idempotent: it uses
        // TryAdd and TryAddEnumerable throughout.
        services.AddThemiaPayments();
        return services;
    }
}
