using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// <see cref="PurposeOptions.InvalidateSiblingsOnVerify"/> (coord #0192): a successful verify voids the other
/// live challenges of the same scope, in the same transaction as the consume. These run the sequential
/// semantics against SQLite; the concurrent one-winner proof is in the integration project, on the real engines.
/// </summary>
public sealed class SiblingVoidTests : IDisposable
{
    private const string Purpose = "password-reset";
    private const string OffPurpose = "password-reset-off";
    private const string TokenPurpose = "invite-link";
    private const string Key = "user-1";

    private readonly SqliteConnection keepAlive;
    private readonly ChallengeService service;

    public SiblingVoidTests()
    {
        var connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);

        var options = new ChallengeOptions();
        options.ConfigurePurpose(Purpose, p => ConfigureNumeric(p, voidSiblings: true));
        options.ConfigurePurpose(OffPurpose, p => ConfigureNumeric(p, voidSiblings: false));
        options.ConfigurePurpose(TokenPurpose, p =>
        {
            ConfigureNumeric(p, voidSiblings: true);
            p.Format = ChallengeFormat.OpaqueToken(32);
        });
        service = new ChallengeService(
            new SqliteChallengeDialect(connString),
            options,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T00:00:00Z")),
            NullLogger<ChallengeService>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    private static void ConfigureNumeric(PurposeOptions p, bool voidSiblings)
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = TimeSpan.FromHours(1);
        p.MaxLiveChallenges = 3;
        p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
        p.InvalidateSiblingsOnVerify = voidSiblings;
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
    public async Task Verify_ShouldVoidTheOlderLink_WhenAResentLinkIsRedeemedFirst()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");
        var first = await IssueAsync(scope);
        var resent = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, resent)).Outcome);

        var late = await service.VerifyAsync(scope, first);
        Assert.Equal(ChallengeVerifyOutcome.Consumed, late.Outcome);
        Assert.Equal(0, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldVoidTheNewerLink_WhenTheOlderLinkIsRedeemedFirst()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");
        var first = await IssueAsync(scope);
        var resent = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, first)).Outcome);

        Assert.NotEqual(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, resent)).Outcome);
    }

    [Fact]
    public async Task VerifyByToken_ShouldVoidTheSibling_WhenOneTokenIsRedeemed()
    {
        var scope = new ChallengeScope(Key, TokenPurpose, "t1");
        var first = await IssueAsync(scope);
        var resent = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyByTokenAsync(resent, TokenPurpose, "t1")).Outcome);

        // A voided row is no longer live, so the token lookup finds nothing: NotFound, not Consumed.
        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await service.VerifyByTokenAsync(first, TokenPurpose, "t1")).Outcome);
        Assert.Equal(0, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldKeepTheSibling_WhenTheOptionIsOff()
    {
        var scope = new ChallengeScope(Key, OffPurpose, "t1");
        var first = await IssueAsync(scope);
        var resent = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, resent)).Outcome);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, first)).Outcome);
    }

    [Fact]
    public async Task Issue_ShouldNotVoidEarlierLinks_WhenTheOptionIsOn()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");

        await IssueAsync(scope);
        await IssueAsync(scope);
        await IssueAsync(scope);

        Assert.Equal(3, LiveCount(scope));
    }

    [Fact]
    public async Task Peek_ShouldNotVoidTheSiblings()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");
        var first = await IssueAsync(scope);
        await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.PeekAsync(scope, first)).Outcome);

        Assert.Equal(2, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldVoidNothing_WhenTheCodeIsWrong()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");
        await IssueAsync(scope);
        await IssueAsync(scope);

        var wrong = await service.VerifyAsync(scope, "000000");

        Assert.NotEqual(ChallengeVerifyOutcome.Verified, wrong.Outcome);
        Assert.Equal(2, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldLeaveOtherScopesUntouched()
    {
        var scope = new ChallengeScope(Key, Purpose, "t1");
        var otherTenant = new ChallengeScope(Key, Purpose, "t2");
        var platform = new ChallengeScope(Key, Purpose, null);
        var otherKey = new ChallengeScope("user-2", Purpose, "t1");
        var otherPurpose = new ChallengeScope(Key, OffPurpose, "t1");
        var code = await IssueAsync(scope);
        await IssueAsync(scope);
        await IssueAsync(otherTenant);
        await IssueAsync(platform);
        await IssueAsync(otherKey);
        await IssueAsync(otherPurpose);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(scope, code)).Outcome);

        Assert.Equal(0, LiveCount(scope));
        Assert.Equal(1, LiveCount(otherTenant));
        Assert.Equal(1, LiveCount(platform));
        Assert.Equal(1, LiveCount(otherKey));
        Assert.Equal(1, LiveCount(otherPurpose));
    }

    [Fact]
    public async Task Verify_ShouldVoidThePlatformSiblingsOnly_WhenTheScopeHasNoTenant()
    {
        var platform = new ChallengeScope(Key, Purpose, null);
        var tenant = new ChallengeScope(Key, Purpose, "platform");
        var code = await IssueAsync(platform);
        await IssueAsync(platform);
        await IssueAsync(tenant);

        Assert.Equal(ChallengeVerifyOutcome.Verified, (await service.VerifyAsync(platform, code)).Outcome);

        Assert.Equal(0, LiveCount(platform));
        Assert.Equal(1, LiveCount(tenant));
    }
}
