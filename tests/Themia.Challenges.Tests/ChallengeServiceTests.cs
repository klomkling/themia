using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// The security-requirement tests for <see cref="ChallengeService"/>. Each one fails if the requirement
/// it names is removed — see the task-7 brief and the design spec's "Security requirements" section.
/// Runs against <see cref="SqliteChallengeDialect"/>, a real-SQLite test double (never shipped); the one
/// thing it cannot prove is true concurrent-connection atomicity across separate database engines — see
/// its remarks — which is Task 8's job against the real dialects.
/// </summary>
public sealed class ChallengeServiceTests : IDisposable
{
    private readonly SqliteConnection keepAlive;
    private readonly string connString;
    private readonly FakeTimeProvider time;

    public ChallengeServiceTests()
    {
        connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);
        time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
    }

    public void Dispose() => keepAlive.Dispose();

    private ChallengeService CreateService(Action<ChallengeOptions> configure)
    {
        var options = new ChallengeOptions();
        configure(options);
        return new ChallengeService(new SqliteChallengeDialect(connString), options, time, NullLogger<ChallengeService>.Instance);
    }

    private static void Configure(
        PurposeOptions p,
        int maxAttempts = 5,
        int maxLiveChallenges = 1,
        TimeSpan? ttl = null,
        (int Limit, TimeSpan Window)? perScopeWindow = null)
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = ttl ?? TimeSpan.FromMinutes(5);
        p.MaxAttempts = maxAttempts;
        p.MaxLiveChallenges = maxLiveChallenges;
        p.PerScopeWindow = perScopeWindow ?? (100, TimeSpan.FromMinutes(15));
    }

    private static int LiveCount(SqliteConnection connection, ChallengeScope scope) =>
        connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM challenges WHERE key = @Key AND purpose = @Purpose AND consumed_at IS NULL",
            new { scope.Key, scope.Purpose });

    // ---- Rate limiting ----------------------------------------------------------------------

    [Fact]
    public async Task Issue_ShouldRateLimit_PerScope()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, perScopeWindow: (2, TimeSpan.FromMinutes(15)))));
        var scope = new ChallengeScope("+66111111111", "login", "tenantA");

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        var third = await service.IssueAsync(scope);

        Assert.Equal(ChallengeIssueOutcome.RateLimited, third.Outcome);
        Assert.Null(third.Secret);
    }

    [Fact]
    public async Task Issue_ShouldRateLimit_PerKeyAcrossPurposes()
    {
        // The per-key ceiling is store-wide, so every purpose necessarily shares one bucket — there is
        // no per-purpose window that could floor the same key into a different one. Each purpose's own
        // PerScopeWindow is generous, so no per-purpose limit is ever the one that trips.
        var service = CreateService(o =>
        {
            o.PerKeyWindow = (3, TimeSpan.FromHours(1));
            o.ConfigurePurpose("login", p => Configure(p));
            o.ConfigurePurpose("reset", p => Configure(p));
            o.ConfigurePurpose("verify", p => Configure(p));
        });
        const string key = "+66222222222";

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(new ChallengeScope(key, "login", "tenantA"))).Outcome);
        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(new ChallengeScope(key, "reset", "tenantA"))).Outcome);
        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(new ChallengeScope(key, "verify", "tenantA"))).Outcome);

        // A 4th purpose never issued against before — its own per-scope count is 0 — still refused,
        // because the per-key ceiling (3) was already reached by the other three purposes.
        var fourth = await service.IssueAsync(new ChallengeScope(key, "login", "tenantA"));

        Assert.Equal(ChallengeIssueOutcome.RateLimited, fourth.Outcome);
    }

    [Fact]
    public async Task Refund_ShouldReturnQuota_SoAFailedDeliveryDoesNotConsumeIt()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, perScopeWindow: (1, TimeSpan.FromMinutes(15)))));
        var scope = new ChallengeScope("+66333333333", "login", "tenantA");

        var issued = await service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, (await service.IssueAsync(scope)).Outcome);

        // The issuance time, not "now": the counters are fixed-width buckets keyed by window start, so
        // only the bucket the issue charged is the one a refund may decrement.
        await service.RefundAsync(scope, issued.IssuedAt!.Value);

        var afterRefund = await service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.Issued, afterRefund.Outcome);
    }

    // ---- Verify outcomes ----------------------------------------------------------------------

    [Fact]
    public async Task Verify_ShouldReturnVerified_ForTheCorrectSecret()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p)));
        var scope = new ChallengeScope("+66444444444", "login", "tenantA");
        var secret = (await service.IssueAsync(scope)).Secret!;

        var result = await service.VerifyAsync(scope, secret);

        Assert.Equal(ChallengeVerifyOutcome.Verified, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Verify_ShouldReturnIncorrect_ForAWrongSecret()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxAttempts: 5)));
        var scope = new ChallengeScope("+66555555555", "login", "tenantA");
        var secret = (await service.IssueAsync(scope)).Secret!;
        var wrong = secret == "000000" ? "111111" : "000000";

        var result = await service.VerifyAsync(scope, wrong);

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Verify_ShouldReturnNotFound_WhenNoChallengeWasEverIssued()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p)));
        var scope = new ChallengeScope("+66878787878", "login", "tenantA");

        var result = await service.VerifyAsync(scope, "000000");

        Assert.Equal(ChallengeVerifyOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldReturnExpired_AfterTheTtl()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, ttl: TimeSpan.FromMinutes(1))));
        var scope = new ChallengeScope("+66666666666", "login", "tenantA");
        var secret = (await service.IssueAsync(scope)).Secret!;

        time.Advance(TimeSpan.FromMinutes(2));
        var result = await service.VerifyAsync(scope, secret);

        Assert.Equal(ChallengeVerifyOutcome.Expired, result.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldReturnAttemptsExhausted_AfterMaxAttempts()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxAttempts: 2)));
        var scope = new ChallengeScope("+66777777777", "login", "tenantA");
        var secret = (await service.IssueAsync(scope)).Secret!;
        var wrong = secret == "000000" ? "111111" : "000000";

        var first = await service.VerifyAsync(scope, wrong);
        var second = await service.VerifyAsync(scope, wrong);

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, first.Outcome);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, second.Outcome);
    }

    /// <summary>
    /// The mundane path, not the race: verify once successfully, then verify again with the exact same
    /// (now-consumed) secret a moment later — a double-submitted form, a refresh after success. Must
    /// report <see cref="ChallengeVerifyOutcome.Consumed"/>, not <see cref="ChallengeVerifyOutcome.NotFound"/>
    /// — the two are distinct, meaningful outcomes precisely so a caller can tell "this code was already
    /// used" from "no such challenge" apart, and collapsing them into <c>NotFound</c> reads as an outage
    /// to any caller building alerting or rate-limit logic on that outcome.
    /// </summary>
    [Fact]
    public async Task Verify_ShouldReturnConsumed_ForASequentialReVerifyOfAnAlreadyUsedCode()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p)));
        var scope = new ChallengeScope("+66898989898", "login", "tenantA");
        var secret = (await service.IssueAsync(scope)).Secret!;

        var first = await service.VerifyAsync(scope, secret);
        var second = await service.VerifyAsync(scope, secret);

        Assert.Equal(ChallengeVerifyOutcome.Verified, first.Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, second.Outcome);
    }

    /// <summary>
    /// The single most important test in the suite: two callers race to verify the same correct secret.
    /// Exactly one must observe <see cref="ChallengeVerifyOutcome.Verified"/>; the other must observe
    /// <see cref="ChallengeVerifyOutcome.Consumed"/>, never a second <c>Verified</c>. Made deterministic
    /// with <see cref="RaceGatingChallengeDialect"/> rather than relying on incidental thread timing —
    /// see its remarks.
    /// </summary>
    [Fact]
    public async Task Verify_ShouldReturnConsumed_OnTheSecondUse()
    {
        var options = new ChallengeOptions();
        options.ConfigurePurpose("login", p => Configure(p));
        var scope = new ChallengeScope("+66888888888", "login", "tenantA");

        var issuingService = new ChallengeService(new SqliteChallengeDialect(connString), options, time, NullLogger<ChallengeService>.Instance);
        var secret = (await issuingService.IssueAsync(scope)).Secret!;

        using var barrier = new Barrier(2);
        var raceDialect = new RaceGatingChallengeDialect(connString, barrier);
        var serviceA = new ChallengeService(raceDialect, options, time, NullLogger<ChallengeService>.Instance);
        var serviceB = new ChallengeService(raceDialect, options, time, NullLogger<ChallengeService>.Instance);

        var taskA = Task.Run(() => serviceA.VerifyAsync(scope, secret));
        var taskB = Task.Run(() => serviceB.VerifyAsync(scope, secret));
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Contains(results, r => r.Outcome == ChallengeVerifyOutcome.Verified);
        Assert.Contains(results, r => r.Outcome == ChallengeVerifyOutcome.Consumed);
    }

    // ---- Re-issue policy ----------------------------------------------------------------------

    [Fact]
    public async Task Issue_ShouldInvalidateTheOutstandingChallenge_WhenMaxLiveIsOne()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxLiveChallenges: 1)));
        var scope = new ChallengeScope("+66999999999", "login", "tenantA");

        var first = await service.IssueAsync(scope);
        var second = await service.IssueAsync(scope);

        // Re-issuing already invalidated the first challenge - only the second is live.
        Assert.Equal(1, LiveCount(keepAlive, scope));

        // The first code stopped working: verifying with it must not succeed.
        var verifyFirst = await service.VerifyAsync(scope, first.Secret!);
        Assert.NotEqual(ChallengeVerifyOutcome.Verified, verifyFirst.Outcome);

        var verifySecond = await service.VerifyAsync(scope, second.Secret!);
        Assert.Equal(ChallengeVerifyOutcome.Verified, verifySecond.Outcome);
    }

    [Fact]
    public async Task Issue_ShouldKeepBothLive_WhenMaxLiveIsTwo()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxLiveChallenges: 2)));
        var scope = new ChallengeScope("+66101010101", "login", "tenantA");

        await service.IssueAsync(scope);
        await service.IssueAsync(scope);

        Assert.Equal(2, LiveCount(keepAlive, scope));
    }

    /// <summary>
    /// The point of <see cref="PurposeOptions.MaxLiveChallenges"/> &gt; 1, proven directly: a
    /// late-arriving first SMS (see its remarks — mobile queues don't preserve send order) must still
    /// verify after a resend. Asserting only that both rows stay un-invalidated (as
    /// <see cref="Issue_ShouldKeepBothLive_WhenMaxLiveIsTwo"/> does) is not enough — it would pass even
    /// if verification only ever looked at the newest row, which is exactly the defect this test guards
    /// against. Fails against a dialect (or a <c>VerifyAsync</c>) that only returns/checks the most
    /// recently issued live challenge for a scope.
    /// </summary>
    [Fact]
    public async Task Verify_ShouldSucceedWithTheOlderCode_WhenMaxLiveIsTwo()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxLiveChallenges: 2)));
        var scope = new ChallengeScope("+66102020202", "login", "tenantA");

        var a = await service.IssueAsync(scope);
        var b = await service.IssueAsync(scope);

        var verifyA = await service.VerifyAsync(scope, a.Secret!);

        Assert.Equal(ChallengeVerifyOutcome.Verified, verifyA.Outcome);

        // b is unaffected by a's consumption - still separately live and verifiable.
        var verifyB = await service.VerifyAsync(scope, b.Secret!);
        Assert.Equal(ChallengeVerifyOutcome.Verified, verifyB.Outcome);
    }

    /// <summary>
    /// Attempt accounting when several challenges are live: a wrong guess must count against every live
    /// row, not just the newest — otherwise an attacker could exhaust the newest row's budget for free
    /// and still have a fresh <see cref="PurposeOptions.MaxAttempts"/> budget left on the older one,
    /// widening the brute-force surface with <see cref="PurposeOptions.MaxLiveChallenges"/>, which the
    /// design spec explicitly rules out.
    /// </summary>
    [Fact]
    public async Task Verify_ShouldRecordAttemptAgainstEveryLiveChallenge_WhenMultipleAreLive()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, maxLiveChallenges: 2, maxAttempts: 2)));
        var scope = new ChallengeScope("+66103030303", "login", "tenantA");

        var a = await service.IssueAsync(scope);
        var b = await service.IssueAsync(scope);
        var wrong = new[] { a.Secret!, b.Secret! }.Contains("000000") ? "111111" : "000000";

        // Two wrong guesses reach MaxAttempts (2) - if it only counted against one row, a's code would
        // still verify afterwards despite two total wrong attempts against the scope.
        var first = await service.VerifyAsync(scope, wrong);
        var second = await service.VerifyAsync(scope, wrong);

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, first.Outcome);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, second.Outcome);

        var verifyA = await service.VerifyAsync(scope, a.Secret!);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, verifyA.Outcome);
    }

    // ---- Tenant isolation ----------------------------------------------------------------------

    [Fact]
    public async Task Verify_ShouldNotMatchAcrossTenants()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p)));
        var scopeA = new ChallengeScope("+66121212121", "login", "tenantA");
        var scopeB = scopeA with { TenantId = "tenantB" };
        var secret = (await service.IssueAsync(scopeA)).Secret!;

        var result = await service.VerifyAsync(scopeB, secret);

        Assert.NotEqual(ChallengeVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task RateLimit_ShouldNotLeakAcrossTenants()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p, perScopeWindow: (1, TimeSpan.FromMinutes(15)))));
        var scopeA = new ChallengeScope("+66131313131", "login", "tenantA");
        var scopeB = scopeA with { TenantId = "tenantB" };

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scopeA)).Outcome);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, (await service.IssueAsync(scopeA)).Outcome);

        var tenantBResult = await service.IssueAsync(scopeB);
        Assert.Equal(ChallengeIssueOutcome.Issued, tenantBResult.Outcome);
    }

    // ---- Opaque token (unshipped) ----------------------------------------------------------------------

    [Fact]
    public async Task VerifyByToken_ShouldThrowNotSupported_InV1()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Configure(p)));

        await Assert.ThrowsAsync<NotSupportedException>(() => service.VerifyByTokenAsync("some-token", "login"));
    }
}
