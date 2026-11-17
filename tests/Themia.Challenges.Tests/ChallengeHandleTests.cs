using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// coord #0232: verifying by a sender-held challenge handle. A stranger who knows only the address must
/// not be able to spend anything the owner's verify depends on.
/// </summary>
public sealed class ChallengeHandleTests : IDisposable
{
    private const string Purpose = "email-otp";
    private const string Tenant = "tenantA";
    private const string Wrong = "000000";

    private readonly SqliteConnection keepAlive;
    private readonly string connString;
    private readonly FakeTimeProvider time;

    public ChallengeHandleTests()
    {
        connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);
        time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
    }

    public void Dispose() => keepAlive.Dispose();

    private ChallengeService CreateService(
        int maxAttempts = 5, int maxLive = 1, int verifyLimit = 3, bool requireHandle = false)
    {
        var options = new ChallengeOptions { VerifyWindow = (verifyLimit, TimeSpan.FromMinutes(15)) };
        options.ConfigurePurpose(Purpose, p =>
        {
            p.Format = ChallengeFormat.Numeric(6);
            p.Ttl = TimeSpan.FromMinutes(5);
            p.MaxAttempts = maxAttempts;
            p.MaxLiveChallenges = maxLive;
            p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
            p.RequireHandle = requireHandle;
        });
        return new ChallengeService(new SqliteChallengeDialect(connString), options, time, NullLogger<ChallengeService>.Instance);
    }

    private static ChallengeScope Victim => new("victim@example.com", Purpose, Tenant);

    private static async Task<(Guid Handle, string Secret)> IssueAsync(ChallengeService service, ChallengeScope? scope = null)
    {
        var issued = await service.IssueAsync(scope ?? Victim);
        Assert.True(issued.Succeeded);
        return (issued.ChallengeId!.Value, issued.Secret!);
    }

    private int RateWindowRows() => keepAlive.ExecuteScalar<int>("SELECT COUNT(*) FROM challenge_rate_windows");

    private int Attempts(Guid id) => keepAlive.ExecuteScalar<int>("SELECT attempts FROM challenges WHERE id = @Id", new { Id = id });

    [Fact]
    public async Task VerifyByHandle_ShouldVerifyOnce_WhenTheCodeIsCorrect()
    {
        var service = CreateService();
        var (handle, secret) = await IssueAsync(service);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldStillVerify_WhenAStrangerBurnedTheScopeKeyedWindow()
    {
        // The #0232 repro: garbage codes against the address spend the per-address VerifyWindow.
        var service = CreateService(verifyLimit: 3);
        var (handle, secret) = await IssueAsync(service);

        for (var i = 0; i < 10; i++)
        {
            await service.VerifyAsync(Victim, Wrong);
        }

        Assert.Equal(ChallengeVerifyOutcome.RateLimited, (await service.VerifyAsync(Victim, secret)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldStillVerify_WhenAStrangerBurnedTheirOwnHandleForTheSameKey()
    {
        // Issue is anonymous: the stranger gets a valid handle for the victim's address. Their handle's
        // bucket must be theirs alone. MaxLiveChallenges 2 so the stranger's issue does not void the victim's row.
        var service = CreateService(maxLive: 2, verifyLimit: 3);
        var (victimHandle, victimSecret) = await IssueAsync(service);
        var (strangerHandle, _) = await IssueAsync(service);

        for (var i = 0; i < 20; i++)
        {
            await service.VerifyByHandleAsync(strangerHandle, Purpose, Wrong, Tenant);
        }

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyByHandleAsync(victimHandle, Purpose, victimSecret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldReturnNotFoundAndChargeNothing_WhenTheHandleIsUnknown()
    {
        var service = CreateService();
        await IssueAsync(service);
        var windowsBefore = RateWindowRows();

        var result = await service.VerifyByHandleAsync(Guid.NewGuid(), Purpose, Wrong, Tenant);

        Assert.Equal(ChallengeVerifyOutcome.NotFound, result.Outcome);
        Assert.Equal(windowsBefore, RateWindowRows());
    }

    [Theory]
    [InlineData("other-purpose", Tenant)]
    [InlineData(Purpose, "tenantB")]
    [InlineData(Purpose, null)]
    public async Task VerifyByHandle_ShouldReturnNotFoundAndChargeNothing_WhenPurposeOrTenantDiffer(string purpose, string? tenant)
    {
        var options = new ChallengeOptions();
        options.ConfigurePurpose(Purpose, p => p.Format = ChallengeFormat.Numeric(6));
        options.ConfigurePurpose("other-purpose", p => p.Format = ChallengeFormat.Numeric(6));
        var service = new ChallengeService(new SqliteChallengeDialect(connString), options, time, NullLogger<ChallengeService>.Instance);
        var (handle, secret) = await IssueAsync(service);
        var windowsBefore = RateWindowRows();

        var result = await service.VerifyByHandleAsync(handle, purpose, secret, tenant);

        Assert.Equal(ChallengeVerifyOutcome.NotFound, result.Outcome);
        Assert.Equal(windowsBefore, RateWindowRows());
        Assert.Equal(0, Attempts(handle));
    }

    [Fact]
    public async Task VerifyByHandle_ShouldChargeOnlyTheHandlesRow_WhenTheCodeIsWrong()
    {
        var service = CreateService(maxLive: 2, verifyLimit: 100);
        var (handle, _) = await IssueAsync(service);
        var (sibling, _) = await IssueAsync(service);

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);

        Assert.Equal(1, Attempts(handle));
        Assert.Equal(0, Attempts(sibling));
    }

    [Fact]
    public async Task VerifyByHandle_ShouldReportExhausted_AndRefuseTheCorrectCode_WhenMaxAttemptsIsSpent()
    {
        var service = CreateService(maxAttempts: 2, verifyLimit: 100);
        var (handle, secret) = await IssueAsync(service);

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, (await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldRateLimitWithRetryAfter_WhenTheHandleExceedsTheWindow_AndLeaveScopeKeyedAlone()
    {
        var service = CreateService(maxAttempts: 100, verifyLimit: 2);
        var (handle, secret) = await IssueAsync(service);

        // The Limit-th call is still served; only the one after it is refused.
        Assert.Equal(ChallengeVerifyOutcome.Incorrect, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.Incorrect, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);
        var limited = await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant);

        Assert.Equal(ChallengeVerifyOutcome.RateLimited, limited.Outcome);
        Assert.NotNull(limited.RetryAfter);
        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(Victim, secret)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldReportConsumed_WhenTheHandlesRowWasSupersededByANewerIssue()
    {
        var service = CreateService(maxLive: 1);
        var (oldHandle, oldSecret) = await IssueAsync(service);
        await IssueAsync(service);

        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyByHandleAsync(oldHandle, Purpose, oldSecret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldClassifyFromItsOwnRow_WhenANewerLiveRowExists()
    {
        // consumed own row + a newer live row in the scope: the answer is about the handle's row.
        var service = CreateService(maxLive: 2);
        var (consumed, consumedSecret) = await IssueAsync(service);
        await service.VerifyByHandleAsync(consumed, Purpose, consumedSecret, Tenant);
        await IssueAsync(service);

        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyByHandleAsync(consumed, Purpose, consumedSecret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldReportConsumedAndChargeNothing_WhenTheRowIsSpentAndTheCodeIsWrong()
    {
        var service = CreateService(verifyLimit: 100);
        var (handle, secret) = await IssueAsync(service);
        await service.VerifyByHandleAsync(handle, Purpose, secret, Tenant);

        Assert.Equal(ChallengeVerifyOutcome.Consumed, (await service.VerifyByHandleAsync(handle, Purpose, Wrong, Tenant)).Outcome);
        Assert.Equal(0, Attempts(handle));
    }

    [Fact]
    public async Task VerifyByHandle_ShouldReportExpired_WhenTheTtlPassed_EvenWithANewerLiveRow()
    {
        var service = CreateService(maxLive: 2);
        var (old, oldSecret) = await IssueAsync(service);
        time.Advance(TimeSpan.FromMinutes(4));
        await IssueAsync(service);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(ChallengeVerifyOutcome.Expired, (await service.VerifyByHandleAsync(old, Purpose, oldSecret, Tenant)).Outcome);
    }

    [Fact]
    public async Task VerifyByHandle_ShouldVerifyExactlyOnce_WhenSubmittedConcurrently()
    {
        var service = CreateService(verifyLimit: 100);
        var (handle, secret) = await IssueAsync(service);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => service.VerifyByHandleAsync(handle, Purpose, secret, Tenant))));

        Assert.Equal(1, results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified));
    }

    [Fact]
    public async Task ScopeKeyedVerifyAndPeek_ShouldThrow_WhenThePurposeRequiresAHandle()
    {
        var service = CreateService(requireHandle: true);
        var (_, secret) = await IssueAsync(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(Victim, secret));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PeekAsync(Victim, secret));
    }

    [Fact]
    public void ConfigurePurpose_ShouldRejectTheReservedHandleBucketName()
    {
        var options = new ChallengeOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigurePurpose(ChallengeOptions.HandleVerifyBucketPurpose, _ => { }));
    }
}
