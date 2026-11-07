using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.SecondFactor.DependencyInjection;

/// <summary>DI entry point for the engine-neutral second-factor pieces (policy, recovery-code hasher).</summary>
public static class SecondFactorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMfaPolicy"/> over the rules in <paramref name="configure"/> and the keyed
    /// <see cref="RecoveryCodeHasher"/>. The store comes from the engine package (for example
    /// <c>AddThemiaSecondFactorPostgres</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="recoveryCodeKey">A server-side secret of at least 32 bytes, from configuration or a secret manager.</param>
    /// <param name="configure">Per-app and per-role rules; omitted means second factor is optional for everyone.</param>
    public static IServiceCollection AddThemiaSecondFactor(
        this IServiceCollection services, byte[] recoveryCodeKey, Action<MfaPolicyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(recoveryCodeKey);

        var options = new MfaPolicyOptions();
        configure?.Invoke(options);

        services.TryAddSingleton<IMfaPolicy>(new MfaPolicy(options));
        services.TryAddSingleton(new RecoveryCodeHasher(recoveryCodeKey));
        return services;
    }
}
