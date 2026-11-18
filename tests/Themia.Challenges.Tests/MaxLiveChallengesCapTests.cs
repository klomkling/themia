using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// <see cref="PurposeOptions.MaxLiveChallenges"/> above 1 is a real cap (coord #0231): issuing past it voids the
/// oldest live challenges, so a scope never holds more than N verifiable codes at once and the guess budget
/// (MaxAttempts x live codes) has a hard ceiling rather than only the issue-window bound.
/// </summary>
public sealed class MaxLiveChallengesCapTests : IDisposable
{
    private const string CappedPurpose = "email-login";
    private const string SinglePurpose = "single";
    private const string Key = "user-1";
    private const string Tenant = "t1";
    private const int Cap = 2;
    private const int IssuedCount = 5;

    private readonly SqliteConnection keepAlive;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-18T00:00:00Z"));
    private readonly ChallengeService service;

    public MaxLiveChallengesCapTests()
    {
        var connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);

        var options = new ChallengeOptions();
        options.ConfigurePurpose(CappedPurpose, p => Configure(p, Cap));
        options.ConfigurePurpose(SinglePurpose, p => Configure(p, 1));
        service = new ChallengeService(
            new SqliteChallengeDialect(connString), options, clock, NullLogger<ChallengeService>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    private static void Configure(PurposeOptions p, int maxLive)
    {
        p.Format = ChallengeFormat.Numeric(8);
        p.Ttl = TimeSpan.FromHours(1);
        p.MaxLiveChallenges = maxLive;
        p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
    }

    private int LiveCount(ChallengeScope scope) =>
        keepAlive.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM challenges WHERE key = @Key AND purpose = @Purpose AND tenant_id IS @TenantId AND consumed_at IS NULL",
            new { scope.Key, scope.Purpose, scope.TenantId });

    private async Task<List<string>> IssueAsync(ChallengeScope scope, int count)
    {
        var secrets = new List<string>();
        for (var i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var issued = await service.IssueAsync(scope);
            Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
            secrets.Add(issued.Secret!);
        }

        return secrets;
    }

    [Fact]
    public async Task Issue_ShouldKeepOnlyTheNewestNLive_WhenMaxLiveChallengesIsAboveOne()
    {
        var scope = new ChallengeScope(Key, CappedPurpose, Tenant);

        await IssueAsync(scope, IssuedCount);

        Assert.Equal(Cap, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldAcceptOnlyTheNewestNCodes_AfterIssuingPastTheCap()
    {
        var scope = new ChallengeScope(Key, CappedPurpose, Tenant);
        var secrets = await IssueAsync(scope, IssuedCount);

        var outcomes = new List<ChallengeVerifyOutcome>();
        foreach (var secret in secrets)
        {
            outcomes.Add((await service.VerifyAsync(scope, secret)).Outcome);
        }

        var evicted = IssuedCount - Cap;
        Assert.All(outcomes.Take(evicted), outcome => Assert.NotEqual(ChallengeVerifyOutcome.Verified, outcome));
        Assert.All(outcomes.Skip(evicted), outcome => Assert.Equal(ChallengeVerifyOutcome.Verified, outcome));
    }

    [Fact]
    public async Task Issue_ShouldKeepEveryCode_WhenFewerThanTheCapAreLive()
    {
        var scope = new ChallengeScope(Key, CappedPurpose, Tenant);

        var secrets = await IssueAsync(scope, Cap);

        Assert.Equal(Cap, LiveCount(scope));
        foreach (var secret in secrets)
        {
            Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, secret)).Outcome);
        }
    }

    [Fact]
    public async Task Issue_ShouldNotTouchOtherScopes_WhenCappingOne()
    {
        var scope = new ChallengeScope(Key, CappedPurpose, Tenant);
        var otherKey = new ChallengeScope("user-2", CappedPurpose, Tenant);
        await IssueAsync(otherKey, Cap);

        await IssueAsync(scope, IssuedCount);

        Assert.Equal(Cap, LiveCount(otherKey));
    }

    [Fact]
    public async Task Issue_ShouldStillKeepOnlyTheNewest_WhenMaxLiveChallengesIsOne()
    {
        var scope = new ChallengeScope(Key, SinglePurpose, Tenant);

        await IssueAsync(scope, IssuedCount);

        Assert.Equal(1, LiveCount(scope));
    }
}
