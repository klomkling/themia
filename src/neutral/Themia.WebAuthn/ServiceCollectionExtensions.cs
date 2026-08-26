using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.WebAuthn;

/// <summary>Registers <see cref="IWebAuthnService"/> and its required challenge store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WebAuthn ceremonies with <typeparamref name="TChallengeStore"/> holding the in-flight
    /// ceremony.
    /// </summary>
    /// <typeparam name="TChallengeStore">
    /// The challenge store. <b>Required by the signature on purpose</b> — there is no overload without
    /// it and no default. A process-local default would break every multi-instance deployment, and a
    /// reusable one would let a signed response be replayed. Same reasoning as
    /// <c>AddThemiaTotp&lt;TReplayStore&gt;</c>: a type parameter cannot be forgotten and cannot be
    /// ordered wrong, where a registration-time check would fail for a caller who registers their
    /// store afterwards.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Relying-party identity and ceremony settings. Required: the domain and origins have no sensible default.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaWebAuthn<TChallengeStore>(
        this IServiceCollection services,
        Action<WebAuthnOptions> configure)
        where TChallengeStore : class, IWebAuthnChallengeStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // Scoped: a challenge store commonly needs a scoped dependency. TryAdd so a caller who
        // registered it themselves, at whatever lifetime suits their store, keeps their registration.
        services.TryAddScoped<IWebAuthnChallengeStore, TChallengeStore>();

        services.TryAddSingleton<IFido2>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebAuthnOptions>>().Value;
            return new Fido2(new Fido2Configuration
            {
                ServerDomain = options.ServerDomain,
                ServerName = options.ServerName,
                Origins = options.Origins,
                TimestampDriftTolerance = (int)options.ChallengeTimeout.TotalMilliseconds,
            });
        });

        services.TryAddScoped<IWebAuthnService, WebAuthnService>();

        return services;
    }
}
