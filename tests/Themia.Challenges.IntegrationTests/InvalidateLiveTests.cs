using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Challenges.Internal;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// <see cref="IChallengeService.InvalidateLiveAsync"/> (coord #0195) on the real engines: it voids exactly one scope,
/// numeric and token links alike, and a redeem racing it never gives two winners. The interleaving is forced by
/// <see cref="SiblingGateDialect"/>, because a bare <c>Task.WhenAll</c> can run the two calls one after the other.
/// </summary>
public abstract class InvalidateLiveTests
{
    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the suite over one engine's <paramref name="fixture"/>.</summary>
    protected InvalidateLiveTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    /// <summary>All live links of the scope are voided and counted; a second call finds nothing.</summary>
    [Fact]
    public async Task InvalidateLive_VoidsEveryLiveLinkOfTheScope()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
        var codes = new[] { await IssueAsync(scope), await IssueAsync(scope), await IssueAsync(scope) };

        Assert.Equal(3, await fixture.Service.InvalidateLiveAsync(scope));

        foreach (var code in codes)
        {
            Assert.Equal(ChallengeVerifyOutcome.Consumed, (await fixture.Service.VerifyAsync(scope, code)).Outcome);
        }

        Assert.Equal(0, await fixture.Service.InvalidateLiveAsync(scope));
    }

    /// <summary>Another key, purpose or tenant, and a platform scope against a tenant literally named "platform", stay live.</summary>
    [Fact]
    public async Task InvalidateLive_LeavesOtherScopesUntouched()
    {
        var key = $"key-{Guid.NewGuid():N}";
        var target = new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, "tenant-a");
        var others = new[]
        {
            new ChallengeScope($"other-{key}", ChallengeEngineFixture.SiblingPurpose, "tenant-a"),
            new ChallengeScope(key, ChallengeEngineFixture.SiblingTokenPurpose, "tenant-a"),
            new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, "tenant-b"),
            new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, null),
            new ChallengeScope(key, ChallengeEngineFixture.SiblingPurpose, "platform"),
        };
        await IssueAsync(target);
        var codes = new List<string>();
        foreach (var other in others)
        {
            codes.Add(await IssueAsync(other));
        }

        Assert.Equal(1, await fixture.Service.InvalidateLiveAsync(target));

        for (var i = 0; i < others.Length; i++)
        {
            Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(others[i], codes[i])).Outcome);
        }
    }

    /// <summary>
    /// A voided opaque-token link answers NotFound by token and Consumed by scope, for the verify and the peek with the
    /// correct secret: an app that knows the key redeems by scope, not by token.
    /// </summary>
    [Fact]
    public async Task InvalidateLive_VoidsAnOpaqueLink_ForTokenScopeVerifyAndPeek()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingTokenPurpose);
        var token = await IssueAsync(scope);

        Assert.Equal(1, await fixture.Service.InvalidateLiveAsync(scope));

        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await fixture.Service.VerifyByTokenAsync(token, scope.Purpose, scope.TenantId)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await fixture.Service.PeekAsync(scope, token)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await fixture.Service.VerifyAsync(scope, token)).Outcome);
    }

    /// <summary>The scope is usable afterwards: a fresh link issues and verifies, so no quota moved.</summary>
    [Fact]
    public async Task InvalidateLive_LeavesTheScopeUsable()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
        await IssueAsync(scope);
        await fixture.Service.InvalidateLiveAsync(scope);

        var fresh = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(scope, fresh)).Outcome);
    }

    /// <summary>
    /// A redeem (siblings on, three live rows) and a void of the scope are forced to meet: the redeem has consumed its
    /// row, uncommitted, when both ask for the void statement. Never two winners, no exception, and every row is dead
    /// afterwards. Which side loses a deadlock is not controllable, so the voided count is only bounded: 0 to 2 when the redeem won, 3 when it lost.
    /// </summary>
    [Fact]
    public async Task ARedeemRacingTheVoid_NeverGivesTwoWinners_AndLeavesNothingLive()
    {
        for (var round = 0; round < 5; round++)
        {
            var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
            var codes = new[] { await IssueAsync(scope), await IssueAsync(scope), await IssueAsync(scope) };

            using var voidGate = new Barrier(2);
            var gated = new SiblingGateDialect(fixture.Dialect, beforeConsume: null, beforeVoid: voidGate);
            var redeeming = NewService(gated);
            var voiding = NewService(gated);

            var redeem = redeeming.VerifyAsync(scope, codes[0]);
            // On a thread of its own: the call reaches the gate before its first await, and must not hold the test thread.
            var voided = Task.Run(() => voiding.InvalidateLiveAsync(scope));
            await Task.WhenAll(redeem, voided);

            var outcome = (await redeem).Outcome;
            var count = await voided;
            Assert.True(
                outcome is ChallengeVerifyOutcome.Verified or ChallengeVerifyOutcome.Consumed,
                $"round {round}: the redeem answered {outcome}");
            if (outcome == ChallengeVerifyOutcome.Verified)
            {
                Assert.InRange(count, 0, 2);   // the redeem committed its row, so the void cannot also have taken it
            }
            else
            {
                Assert.Equal(3, count);   // the redeem lost and rolled back, so the void took all three
            }

            Assert.Equal(0, await fixture.Service.InvalidateLiveAsync(scope));
            foreach (var code in codes)
            {
                Assert.NotEqual(ChallengeVerifyOutcome.Verified, (await fixture.Service.VerifyAsync(scope, code)).Outcome);
            }
        }
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

    /// <summary>PostgreSQL execution of <see cref="InvalidateLiveTests"/>.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresInvalidateLiveTests : InvalidateLiveTests
    {
        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresInvalidateLiveTests(PostgresChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>MySQL execution of <see cref="InvalidateLiveTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlInvalidateLiveTests : InvalidateLiveTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlInvalidateLiveTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>SQL Server execution of <see cref="InvalidateLiveTests"/>.</summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerInvalidateLiveTests : InvalidateLiveTests
    {
        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerInvalidateLiveTests(SqlServerChallengeFixture fixture) : base(fixture)
        {
        }
    }
}
