using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Challenges.Internal;

namespace Themia.Challenges.DependencyInjection;

/// <summary>DI entry point for the <c>Themia.Challenges</c> policy engine.</summary>
public static class ChallengeServiceCollectionExtensions
{
    /// <summary>
    /// Registers validated <see cref="ChallengeOptions"/>, <see cref="TimeProvider.System"/>, and
    /// <see cref="IChallengeService"/>. Does <b>not</b> register an <see cref="IChallengeDialect"/> —
    /// call exactly one engine package's registration method (<c>AddThemiaChallengesPostgres</c>,
    /// <c>AddThemiaChallengesMySql</c>, or <c>AddThemiaChallengesSqlServer</c>) as well, in either order.
    /// </summary>
    /// <remarks>
    /// The dialect is checked when <see cref="IChallengeService"/> is first resolved, not here. Engine
    /// packages register their <see cref="IChallengeDialect"/> independently of this call — often after
    /// it — so scanning <paramref name="services"/> for one at this point would reject every valid
    /// registration order that calls the engine method second. Checking lazily at first resolution
    /// works regardless of call order and still fails loudly, just later than "call time": at "first use
    /// time" instead of silently falling through to whatever default behaviour a missing dependency
    /// would otherwise produce.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the purposes this instance issues and verifies challenges for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThemiaChallenges(this IServiceCollection services, Action<ChallengeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ChallengeOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IChallengeService>(sp =>
        {
            var dialect = sp.GetService<IChallengeDialect>() ?? throw new InvalidOperationException(
                "Themia.Challenges has no IChallengeDialect registered. Call exactly one of " +
                "AddThemiaChallengesPostgres(...), AddThemiaChallengesMySql(...), or " +
                "AddThemiaChallengesSqlServer(...) alongside AddThemiaChallenges(...) to register an engine.");

            var logger = sp.GetService<ILogger<ChallengeService>>() ?? NullLogger<ChallengeService>.Instance;
            return new ChallengeService(dialect, sp.GetRequiredService<ChallengeOptions>(), sp.GetRequiredService<TimeProvider>(), logger);
        });

        return services;
    }
}
