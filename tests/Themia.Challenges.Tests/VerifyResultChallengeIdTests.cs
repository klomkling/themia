using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// A verified code names its challenge (coord #0230), so a caller that kept no state between issue and verify
/// can refund the issue-window charge after a successful sign-in.
/// </summary>
public sealed class VerifyResultChallengeIdTests : IDisposable
{
    private const string Purpose = "email-login";
    private const string Key = "user-1";
    private const string Tenant = "t1";

    private readonly SqliteConnection keepAlive;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-18T00:00:00Z"));
    private readonly ChallengeService service;
    private readonly ChallengeScope scope = new(Key, Purpose, Tenant);

    public VerifyResultChallengeIdTests()
    {
        var connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);

        var options = new ChallengeOptions();
        options.ConfigurePurpose(Purpose, p =>
        {
            p.Format = ChallengeFormat.Numeric(8);
            p.Ttl = TimeSpan.FromHours(1);
            p.PerScopeWindow = (1, TimeSpan.FromHours(1));
        });
        service = new ChallengeService(
            new SqliteChallengeDialect(connString), options, clock, NullLogger<ChallengeService>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    [Fact]
    public async Task Verify_ShouldCarryTheIssuedChallengeId_WhenTheCodeIsAccepted()
    {
        var issued = await service.IssueAsync(scope);

        var result = await service.VerifyAsync(scope, issued.Secret!);

        Assert.Equal(ChallengeVerifyOutcome.Verified, result.Outcome);
        Assert.Equal(issued.ChallengeId, result.ChallengeId);
    }

    [Fact]
    public async Task Peek_ShouldCarryTheIssuedChallengeId_WhenTheCodeMatches()
    {
        var issued = await service.IssueAsync(scope);

        var result = await service.PeekAsync(scope, issued.Secret!);

        Assert.Equal(issued.ChallengeId, result.ChallengeId);
    }

    [Fact]
    public async Task Verify_ShouldCarryNoChallengeId_WhenTheCodeIsWrong()
    {
        await service.IssueAsync(scope);

        var result = await service.VerifyAsync(scope, "00000000");

        Assert.Equal(ChallengeVerifyOutcome.Incorrect, result.Outcome);
        Assert.Null(result.ChallengeId);
    }

    [Fact]
    public async Task Refund_ShouldReopenTheIssueWindow_WhenGivenAVerifiedResultsChallengeId()
    {
        var issued = await service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, (await service.IssueAsync(scope)).Outcome);
        var verified = await service.VerifyAsync(scope, issued.Secret!);

        var refunded = await service.RefundAsync(verified.ChallengeId!.Value);

        Assert.True(refunded);
        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
    }
}
