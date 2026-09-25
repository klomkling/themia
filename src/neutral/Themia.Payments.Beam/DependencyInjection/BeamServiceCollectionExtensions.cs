using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Payments.DependencyInjection;

namespace Themia.Payments.Beam.DependencyInjection;

/// <summary>DI entry point for the Beam adapter.</summary>
public static class BeamServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="BeamOptions"/> (validated with <c>ValidateOnStart</c>), the named
    /// <see cref="HttpClient"/>, and <see cref="BeamPaymentGateway"/> as <see cref="IPaymentGateway"/> and
    /// <see cref="IPaymentGatewayCapabilities"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the merchant id, API key and environment.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaPaymentsBeam(
        this IServiceCollection services, Action<BeamOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BeamOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.MerchantId), "BeamOptions.MerchantId must be set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "BeamOptions.ApiKey must be set.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "BeamOptions.Timeout must be positive.")
            .ValidateOnStart();

        services.AddHttpClient(BeamPaymentGateway.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<BeamOptions>>().Value;
            client.BaseAddress = BeamOptions.BaseAddressFor(options.Environment);
            client.Timeout = options.Timeout;
        });

        services.TryAddSingleton<BeamPaymentGateway>();
        services.TryAddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<BeamPaymentGateway>());
        services.TryAddSingleton<IPaymentGatewayCapabilities>(sp => sp.GetRequiredService<BeamPaymentGateway>());

        // So the gate exists even when the host forgot the core call. AddThemiaPayments is idempotent:
        // it uses TryAdd and TryAddEnumerable throughout.
        services.AddThemiaPayments();
        return services;
    }
}
