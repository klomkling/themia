using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// <see cref="IChallengeService.InvalidateLiveAsync"/> (coord #0195): voids every live challenge of exactly one scope on demand,
/// numeric and opaque-token alike, in one statement, without touching other scopes and without counting as an attempt.
/// The concurrent behaviour against a redeem is in the integration project, on the real engines.
/// </summary>
public sealed class InvalidateLiveTests : IDisposable
{
    private const string Purpose = "password-reset";
    private const string OtherPurpose = "invite";
    private const string TokenPurpose = "reset-link";
    private const string Key = "user-1";
    private const string Tenant = "t1";

    private readonly SqliteConnection keepAlive;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-04T00:00:00Z"));
    private readonly FaultInjectingDialect dialect;
    private readonly ChallengeService service;

    public InvalidateLiveTests()
    {
        var connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);

        var options = new ChallengeOptions();
        options.ConfigurePurpose(Purpose, p => ConfigureNumeric(p));
        options.ConfigurePurpose(OtherPurpose, p => ConfigureNumeric(p));
        options.ConfigurePurpose(TokenPurpose, p =>
        {
            ConfigureNumeric(p);
            p.Format = ChallengeFormat.OpaqueToken(32);
        });
        dialect = new FaultInjectingDialect(new SqliteChallengeDialect(connString));
        service = new ChallengeService(dialect, options, clock, NullLogger<ChallengeService>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    private static void ConfigureNumeric(PurposeOptions p)
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = TimeSpan.FromHours(1);
        p.MaxLiveChallenges = 3;
        p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
    }

    private int LiveCount(ChallengeScope scope) =>
        keepAlive.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM challenges WHERE key = @Key AND purpose = @Purpose AND tenant_id IS @TenantId AND consumed_at IS NULL",
            new { scope.Key, scope.Purpose, scope.TenantId });

    private async Task<string> IssueAsync(ChallengeScope scope)
    {
        var issued = await service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        return issued.Secret!;
    }

    [Fact]
    public async Task InvalidateLive_ShouldVoidEveryLiveChallengeOfTheScope_AndReturnTheCount()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        var secrets = new[] { await IssueAsync(scope), await IssueAsync(scope), await IssueAsync(scope) };

        var voided = await service.InvalidateLiveAsync(scope);

        Assert.Equal(3, voided);
        Assert.Equal(0, LiveCount(scope));
        foreach (var secret in secrets)
        {
            Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyAsync(scope, secret)).Outcome);
        }
    }

    [Fact]
    public async Task InvalidateLive_ShouldReturnZero_WhenNothingIsLive_AndWhenCalledAgain()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        Assert.Equal(0, await service.InvalidateLiveAsync(scope));

        await IssueAsync(scope);
        Assert.Equal(1, await service.InvalidateLiveAsync(scope));
        Assert.Equal(0, await service.InvalidateLiveAsync(scope));
    }

    [Fact]
    public async Task InvalidateLive_ShouldLeaveOtherKeysPurposesAndTenantsAlone()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        var otherKey = new ChallengeScope("user-2", Purpose, Tenant);
        var otherPurpose = new ChallengeScope(Key, OtherPurpose, Tenant);
        var otherTenant = new ChallengeScope(Key, Purpose, "t2");
        var platform = new ChallengeScope(Key, Purpose);
        await IssueAsync(scope);
        var keys = new[] { otherKey, otherPurpose, otherTenant, platform };
        var secrets = new List<string>();
        foreach (var other in keys)
        {
            secrets.Add(await IssueAsync(other));
        }

        Assert.Equal(1, await service.InvalidateLiveAsync(scope));

        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Equal(1, LiveCount(keys[i]));
            Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(keys[i], secrets[i])).Outcome);
        }
    }

    [Fact]
    public async Task InvalidateLive_ShouldTreatAPlatformScopeAndATenantScopeAsDifferent()
    {
        var tenant = new ChallengeScope(Key, Purpose, Tenant);
        var platform = new ChallengeScope(Key, Purpose);
        await IssueAsync(tenant);
        await IssueAsync(platform);

        Assert.Equal(1, await service.InvalidateLiveAsync(platform));

        Assert.Equal(0, LiveCount(platform));
        Assert.Equal(1, LiveCount(tenant));
    }

    [Fact]
    public async Task InvalidateLive_ShouldNotCountChallengesThatAreExpiredOrAlreadyConsumed()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        var consumed = await IssueAsync(scope);
        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, consumed)).Outcome);
        await IssueAsync(scope);
        clock.Advance(TimeSpan.FromHours(2));   // the second one expires
        await IssueAsync(scope);

        Assert.Equal(1, await service.InvalidateLiveAsync(scope));
    }

    [Fact]
    public async Task InvalidateLive_ShouldVoidAnOpaqueTokenChallenge_ForEveryWayItCanBeRedeemed()
    {
        var scope = new ChallengeScope(Key, TokenPurpose, Tenant);
        var token = await IssueAsync(scope);

        Assert.Equal(1, await service.InvalidateLiveAsync(scope));

        // By token a voided row is no longer live (NotFound); by scope, which is how an app that knows the key redeems it,
        // the row is found and answers Consumed, for the verify and for the peek, with the correct secret.
        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await service.VerifyByTokenAsync(token, TokenPurpose, Tenant)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.PeekAsync(scope, token)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyAsync(scope, token)).Outcome);
    }

    [Fact]
    public async Task InvalidateLive_ShouldNotMoveAnyQuota_SoTheScopeIsUsableAfterwards()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        await IssueAsync(scope);
        var attemptsBefore = keepAlive.ExecuteScalar<int>("SELECT COALESCE(SUM(attempts), 0) FROM challenges");
        var windowsBefore = keepAlive.ExecuteScalar<string>("SELECT group_concat(count) FROM challenge_rate_windows");

        await service.InvalidateLiveAsync(scope);

        Assert.Equal(attemptsBefore, keepAlive.ExecuteScalar<int>("SELECT COALESCE(SUM(attempts), 0) FROM challenges"));
        Assert.Equal(windowsBefore, keepAlive.ExecuteScalar<string>("SELECT group_concat(count) FROM challenge_rate_windows"));
        var fresh = await IssueAsync(scope);
        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, fresh)).Outcome);
    }

    [Fact]
    public async Task InvalidateLive_ShouldRetryAClassifiedDeadlock_AndReturnTheCountOfTheRetry()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        await IssueAsync(scope);
        await IssueAsync(scope);
        dialect.FailInvalidate(2);

        Assert.Equal(2, await service.InvalidateLiveAsync(scope));
        Assert.Equal(0, LiveCount(scope));
    }

    [Fact]
    public async Task InvalidateLive_ShouldPropagateAFailure_WhenTheDialectDoesNotClassifyItAsADeadlock()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        await IssueAsync(scope);
        dialect.Classify = false;
        dialect.FailInvalidate(1);

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => service.InvalidateLiveAsync(scope));
        Assert.Equal(1, LiveCount(scope));
    }

    [Fact]
    public async Task InvalidateLive_ShouldThrow_WhenThePurposeWasNeverConfigured()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvalidateLiveAsync(new ChallengeScope(Key, "never-configured", Tenant)));
    }

    [Fact]
    public async Task InvalidateLive_ShouldThrow_WhenTheScopeIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.InvalidateLiveAsync(null!));
    }

    [Fact]
    public async Task InvalidateLive_ShouldThrow_WhenTheTokenIsAlreadyCancelled()
    {
        var scope = new ChallengeScope(Key, Purpose, Tenant);
        await IssueAsync(scope);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InvalidateLiveAsync(scope, cts.Token));
        Assert.Equal(1, LiveCount(scope));
    }
}
