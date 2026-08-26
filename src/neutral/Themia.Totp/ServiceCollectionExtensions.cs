using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Totp;

/// <summary>Registers <see cref="ITotpService"/> and its required replay store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers TOTP generation and verification, with <typeparamref name="TReplayStore"/> as the
    /// single-use guard.
    /// </summary>
    /// <typeparam name="TReplayStore">
    /// The replay store. <b>Required by the signature on purpose</b> — there is no overload without it
    /// and no default implementation. An in-memory default would hold nothing on a second instance, so
    /// every verification would report correct while the replay window stayed open: the guard would
    /// appear to work, with a green test suite on either side. That is coord #0057's failure
    /// (<c>LoggerEmailSender</c> reporting success without sending) applied to a security control.
    /// <para>
    /// A type parameter rather than a check inside this method: a registration-time check would fail
    /// for a caller who registers their store <i>after</i> calling this, which is the ordering trap
    /// coord #0100 reported. This one cannot compile without a store.
    /// </para>
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional code shape and window configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaTotp<TReplayStore>(
        this IServiceCollection services,
        Action<TotpOptions>? configure = null)
        where TReplayStore : class, ITotpReplayStore
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: a replay store commonly needs a scoped dependency (a DbContext, a per-request
        // connection). TryAdd so a caller who registered it themselves, at whatever lifetime suits
        // their store, keeps their registration.
        services.TryAddScoped<ITotpReplayStore, TReplayStore>();
        services.TryAddSingleton(TimeProvider.System);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<TotpOptions>(_ => { });
        }

        services.TryAddScoped<ITotpService, TotpService>();

        return services;
    }
}
