using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Themia.Totp;
using Themia.WebAuthn;

namespace Themia.SecondFactor.DependencyInjection;

/// <summary>DI entry point for the engine-neutral second-factor layer.</summary>
public static class SecondFactorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISecondFactorService"/>, <see cref="IMfaPolicy"/> over <see cref="SecondFactorOptions.Policy"/>,
    /// the keyed <see cref="RecoveryCodeHasher"/>, and the TOTP and WebAuthn services wired to this layer's stores. The
    /// store comes from the engine package (for example <c>AddThemiaSecondFactorPostgres</c>). The adopter also registers
    /// <see cref="ISecondFactorSecretProtector"/>, <see cref="ISecondFactorContactResolver"/>, one
    /// <see cref="IOtpDelivery"/> per channel it offers, and <c>AddThemiaChallenges*</c> with the
    /// <see cref="SecondFactorOptions.EmailOtpPurpose"/> / <see cref="SecondFactorOptions.SmsOtpPurpose"/> purposes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="recoveryCodeKey">A server-side secret of at least 32 bytes, from configuration or a secret manager.</param>
    /// <param name="configure">Issuer, limits and the per-app/per-role policy.</param>
    /// <param name="configureWebAuthn">Relying-party identity: domain, name and origins have no sensible default.</param>
    public static IServiceCollection AddThemiaSecondFactor(
        this IServiceCollection services,
        byte[] recoveryCodeKey,
        Action<SecondFactorOptions> configure,
        Action<WebAuthnOptions> configureWebAuthn)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(recoveryCodeKey);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configureWebAuthn);

        services.AddOptions<SecondFactorOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SecondFactorOptions>, SecondFactorOptionsValidator>());
        services.AddSingleton<IValidateOptions<SecondFactorOptions>>(new StoreOwnershipValidator(services));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new RecoveryCodeHasher(recoveryCodeKey));
        services.TryAddSingleton<IMfaPolicy>(sp => new MfaPolicy(sp.GetRequiredService<IOptions<SecondFactorOptions>>().Value.Policy));

        services.AddThemiaTotp<MfaTotpReplayStore>();
        services.AddThemiaWebAuthn<CeremonyBackedWebAuthnStore>(configureWebAuthn);
        services.TryAddScoped<ISecondFactorService, SecondFactorService>();
        return services;
    }

    /// <summary>
    /// Turns on the admin reset of another user's second factor (<see cref="ISecondFactorAdminService"/>). The host registers an
    /// <see cref="IMfaActOnPolicy"/> (there is no default that allows) and, to end the target's sessions, one or more
    /// <see cref="ISecondFactorResetObserver"/>; host start fails without the policy. Needs a store that implements
    /// <see cref="IMfaResetStore"/> (the PostgreSQL package registers one).
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddThemiaSecondFactorAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IValidateOptions<SecondFactorOptions>>(new AdminPolicyValidator(services));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISecondFactorAdminService, SecondFactorAdminService>();
        return services;
    }
}
