using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Challenges.Internal;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// <see cref="PurposeOptions.InvalidateSiblingsOnVerify"/> (coord #0192) on the real engines: a verified link
/// voids its live siblings, and two siblings redeemed at the same moment yield exactly one winner. The
/// interleaving is forced by <see cref="SiblingGateDialect"/>, not left to thread-pool timing.
/// </summary>
public abstract class SiblingVoidTests
{
    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the suite over one engine's <paramref name="fixture"/>.</summary>
    protected SiblingVoidTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    /// <summary>The older link is dead once the newer one is redeemed (the account-takeover case).</summary>
    [Fact]
    public async Task Verify_VoidsTheOlderLink_WhenTheResentLinkIsRedeemed()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
        var older = await IssueAsync(scope);
        var newer = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(scope, newer)).Outcome);

        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await fixture.Service.VerifyAsync(scope, older)).Outcome);
    }

    /// <summary>Same through the opaque-token path; a voided row is not live, so the lookup finds nothing.</summary>
    [Fact]
    public async Task VerifyByToken_VoidsTheOlderLink_WhenTheResentLinkIsRedeemed()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingTokenPurpose);
        var older = await IssueAsync(scope);
        var newer = await IssueAsync(scope);

        var redeemed = await fixture.Service.VerifyByTokenAsync(newer, scope.Purpose, scope.TenantId);
        Assert.Equal(ChallengeVerifyOutcome.Verified, redeemed.Outcome);

        var late = await fixture.Service.VerifyByTokenAsync(older, scope.Purpose, scope.TenantId);
        Assert.Equal(ChallengeVerifyOutcome.NotFound, late.Outcome);
    }

    /// <summary>A platform scope (no tenant) and a tenant literally named "platform" are different scopes.</summary>
    [Fact]
    public async Task Verify_LeavesOtherTenantsAndPlatformScopesUntouched()
    {
        var key = $"key-{Guid.NewGuid():N}";
        var tenantScope = new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, "tenant-a");
        var platformScope = new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, null);
        var literalPlatform = new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, "platform");
        var tenantCode = await IssueAsync(tenantScope);
        var platformCode = await IssueAsync(platformScope);
        var literalCode = await IssueAsync(literalPlatform);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(platformScope, platformCode)).Outcome);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(tenantScope, tenantCode)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(literalPlatform, literalCode)).Outcome);
    }

    /// <summary>
    /// Two siblings of one scope redeemed at once, forced so BOTH have consumed their own row before either
    /// voids: exactly one wins, every run. Red if the consume and the void are not one transaction (each would
    /// autocommit and both would report Verified).
    /// </summary>
    [Fact]
    public async Task TwoSiblingsRedeemedTogether_ExactlyOneWins()
    {
        for (var round = 0; round < 5; round++)
        {
            var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
            var codeA = await IssueAsync(scope);
            var codeB = await IssueAsync(scope);

            using var consumeGate = new Barrier(2);
            using var voidGate = new Barrier(2);
            var gated = new SiblingGateDialect(fixture.Dialect, beforeConsume: consumeGate, beforeVoid: voidGate);
            var serviceA = NewService(gated);
            var serviceB = NewService(gated);

            var results = await Task.WhenAll(serviceA.VerifyAsync(scope, codeA), serviceB.VerifyAsync(scope, codeB));

            var outcomes = string.Join(", ", results.Select(r => r.Outcome));
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified), $"round {round}: [{outcomes}]");
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Consumed), $"round {round}: [{outcomes}]");
        }
    }

    /// <summary>Same through the token path.</summary>
    [Fact]
    public async Task TwoSiblingTokensRedeemedTogether_ExactlyOneWins()
    {
        for (var round = 0; round < 5; round++)
        {
            var scope = NewScope(ChallengeEngineFixture.SiblingTokenPurpose);
            var tokenA = await IssueAsync(scope);
            var tokenB = await IssueAsync(scope);

            using var consumeGate = new Barrier(2);
            using var voidGate = new Barrier(2);
            var gated = new SiblingGateDialect(fixture.Dialect, beforeConsume: consumeGate, beforeVoid: voidGate);
            var serviceA = NewService(gated);
            var serviceB = NewService(gated);

            var results = await Task.WhenAll(
                serviceA.VerifyByTokenAsync(tokenA, scope.Purpose, scope.TenantId),
                serviceB.VerifyByTokenAsync(tokenB, scope.Purpose, scope.TenantId));

            var outcomes = string.Join(", ", results.Select(r => r.Outcome));
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified), $"round {round}: [{outcomes}]");
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Consumed), $"round {round}: [{outcomes}]");
        }
    }

    /// <summary>
    /// A verify that loses the consume (the same link fetched twice) must not void anything: a fresh link
    /// issued meanwhile would otherwise be killed by a loser. Exactly one void is requested for two racers.
    /// </summary>
    [Fact]
    public async Task TheSameLinkRedeemedTwice_OnlyTheWinnerVoids()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingTokenPurpose);
        var token = await IssueAsync(scope);

        using var consumeGate = new Barrier(2);
        var gated = new SiblingGateDialect(fixture.Dialect, beforeConsume: consumeGate, beforeVoid: null);
        var serviceA = NewService(gated);
        var serviceB = NewService(gated);

        var results = await Task.WhenAll(
            serviceA.VerifyByTokenAsync(token, scope.Purpose, scope.TenantId),
            serviceB.VerifyByTokenAsync(token, scope.Purpose, scope.TenantId));

        Assert.Equal(1, results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified));
        Assert.Equal(1, gated.VoidCalls);
    }

    private ChallengeService NewService(IChallengeDialect dialect) =>
        new(dialect, fixture.Options, fixture.TimeProvider, NullLogger<ChallengeService>.Instance);

    private async Task<string> IssueAsync(ChallengeScope scope)
    {
        var issued = await fixture.Service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        return issued.Secret!;
    }

    private static ChallengeScope NewScope(string purpose) => new($"key-{Guid.NewGuid():N}", purpose, $"tenant-{Guid.NewGuid():N}");

    /// <summary>PostgreSQL execution of <see cref="SiblingVoidTests"/>.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresSiblingVoidTests : SiblingVoidTests
    {
        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresSiblingVoidTests(PostgresChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>MySQL execution of <see cref="SiblingVoidTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlSiblingVoidTests : SiblingVoidTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlSiblingVoidTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>SQL Server execution of <see cref="SiblingVoidTests"/>.</summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerSiblingVoidTests : SiblingVoidTests
    {
        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerSiblingVoidTests(SqlServerChallengeFixture fixture) : base(fixture)
        {
        }
    }
}
