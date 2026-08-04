using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Challenges.DependencyInjection;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// Proves the mandatory-dialect guard: <c>AddThemiaChallenges</c> alone must not silently produce a
/// broken <see cref="IChallengeService"/> — resolving it without an engine package must fail loudly and
/// name the call the adopter is missing, not surface a raw DI activation error.
/// </summary>
public sealed class RegistrationTests
{
    private static ServiceCollection CoreOnly()
    {
        var services = new ServiceCollection();
        services.AddThemiaChallenges(o => o.ConfigurePurpose("login", p => { }));
        return services;
    }

    [Fact]
    public void ResolvingIChallengeService_ShouldThrow_WhenNoDialectIsRegistered()
    {
        var provider = CoreOnly().BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChallengeService>());

        Assert.Contains("AddThemiaChallengesPostgres", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddThemiaChallengesMySql", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddThemiaChallengesSqlServer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvingIChallengeService_ShouldSucceed_WhenACoreOnlyDialectIsRegistered()
    {
        var services = CoreOnly();
        services.AddSingleton<IChallengeDialect>(new SqliteChallengeDialect("Data Source=:memory:"));
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IChallengeService>();

        Assert.NotNull(service);
    }

    [Fact]
    public void AddThemiaChallenges_ShouldThrow_WhenServicesIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ChallengeServiceCollectionExtensions.AddThemiaChallenges(null!, _ => { }));
    }

    [Fact]
    public void AddThemiaChallenges_ShouldThrow_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddThemiaChallenges(null!));
    }

    // The purge hosted service must be registered even before an engine package registers a dialect —
    // ChallengePurgeService's own dialect dependency is optional (defaults to null), so this must not
    // throw at DI-graph-build time regardless of registration order.
    [Fact]
    public void AddThemiaChallenges_ShouldRegisterThePurgeHostedService()
    {
        var provider = CoreOnly().BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        Assert.Contains(hostedServices, s => s is ChallengePurgeService);
    }
}
