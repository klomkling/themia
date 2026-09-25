using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Payments.DependencyInjection;

/// <summary>DI entry point for the provider-agnostic payment pieces.</summary>
public static class PaymentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ThemiaPaymentsOptions"/> (validated on start) and <see cref="PaymentMethodGate"/>.
    /// Call it <b>after</b> the adapter's own <c>Add…</c>, so the policy can be checked against what that
    /// adapter supports.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the shared options; omit for defaults (no method policy).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaPayments(
        this IServiceCollection services, Action<ThemiaPaymentsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<ThemiaPaymentsOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();

        // Type-based descriptor, never a factory lambda: TryAddEnumerable dedupes on the implementation
        // type, and a factory descriptor has none, so it throws ArgumentException ("indistinguishable from
        // other services registered") at registration — every host would fail to start. Verified on net10.0;
        // Themia.Totp registers its validator the same way for the same reason.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ThemiaPaymentsOptions>, PaymentMethodPolicyValidator>());
        services.TryAddSingleton<PaymentMethodGate>();
        return services;
    }
}
