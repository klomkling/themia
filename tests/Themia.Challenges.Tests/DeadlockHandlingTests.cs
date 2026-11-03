using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// What <see cref="ChallengeService"/> does with an engine deadlock the dialect classifies
/// (<see cref="IChallengeDialect.IsDeadlock"/>) and with failures it does not: an issue re-runs its whole
/// transaction without charging the counters again, a sibling-void verify that loses a deadlock answers
/// <see cref="ChallengeVerifyOutcome.Consumed"/>, and everything else propagates — a commit failure, an unclassified error.
/// </summary>
public sealed class DeadlockHandlingTests : IDisposable
{
    private const string Purpose = "password-reset";
    private const string SingleLivePurpose = "login";

    private readonly SqliteConnection keepAlive;
    private readonly FaultInjectingDialect dialect;
    private readonly ChallengeService service;

    public DeadlockHandlingTests()
    {
        var connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared;Foreign Keys=True";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);
        dialect = new FaultInjectingDialect(new SqliteChallengeDialect(connString));

        var options = new ChallengeOptions();
        options.ConfigurePurpose(Purpose, p =>
        {
            p.Format = ChallengeFormat.Numeric(6);
            p.MaxLiveChallenges = 3;
            p.InvalidateSiblingsOnVerify = true;
            p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
        });
        options.ConfigurePurpose(SingleLivePurpose, p =>
        {
            p.Format = ChallengeFormat.Numeric(6);
            p.PerScopeWindow = (100, TimeSpan.FromMinutes(15));
        });
        service = new ChallengeService(
            dialect, options, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T00:00:00Z")), NullLogger<ChallengeService>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    private int LiveCount(ChallengeScope scope) =>
        keepAlive.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM challenges WHERE key = @Key AND purpose = @Purpose AND consumed_at IS NULL", new { scope.Key, scope.Purpose });

    private int ScopeCharge(ChallengeScope scope) =>
        keepAlive.ExecuteScalar<int>(
            "SELECT COALESCE(SUM(count), 0) FROM challenge_rate_windows WHERE key = @Key AND purpose = @Purpose", new { scope.Key, scope.Purpose });

    [Fact]
    public async Task Issue_ShouldRerunTheTransactionWithoutChargingAgain_WhenTheInsertDeadlocks()
    {
        var scope = new ChallengeScope("user-1", SingleLivePurpose, "t1");
        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        dialect.FailInsert(2);

        var second = await service.IssueAsync(scope);

        Assert.Equal(ChallengeIssueOutcome.Issued, second.Outcome);
        Assert.Equal(1, LiveCount(scope));
        Assert.Equal(2, ScopeCharge(scope));
    }

    [Fact]
    public async Task Issue_ShouldPropagateAndRefundTheCharge_WhenTheFailureIsNotClassifiedAsADeadlock()
    {
        var scope = new ChallengeScope("user-1", SingleLivePurpose, "t1");
        dialect.Classify = false;
        dialect.FailInsert(1);

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => service.IssueAsync(scope));

        Assert.Equal(0, LiveCount(scope));
        Assert.Equal(0, ScopeCharge(scope));
    }

    [Fact]
    public async Task Issue_ShouldRerunTheCharge_WhenTheWindowIncrementDeadlocks()
    {
        var scope = new ChallengeScope("user-1", SingleLivePurpose, "t1");
        dialect.FailIncrement(1);

        var issued = await service.IssueAsync(scope);

        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        Assert.Equal(1, ScopeCharge(scope));
    }

    [Fact]
    public async Task Verify_ShouldAnswerConsumed_WhenTheSiblingVoidLosesADeadlock()
    {
        var scope = new ChallengeScope("user-1", Purpose, "t1");
        var code = (await service.IssueAsync(scope)).Secret!;
        await service.IssueAsync(scope);
        AddTriggerOnSecondUpdate();

        var result = await service.VerifyAsync(scope, code);

        Assert.Equal(ChallengeVerifyOutcome.Consumed, result.Outcome);
        Assert.Equal(2, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldPropagate_WhenTheVoidFailsWithAnUnclassifiedError()
    {
        var scope = new ChallengeScope("user-1", Purpose, "t1");
        var code = (await service.IssueAsync(scope)).Secret!;
        await service.IssueAsync(scope);
        AddTriggerOnSecondUpdate();
        dialect.Classify = false;

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => service.VerifyAsync(scope, code));

        Assert.Equal(2, LiveCount(scope));
    }

    [Fact]
    public async Task Verify_ShouldPropagateAndNotReportVerified_WhenTheCommitFails()
    {
        var scope = new ChallengeScope("user-1", Purpose, "t1");
        var code = (await service.IssueAsync(scope)).Secret!;
        await service.IssueAsync(scope);
        // A deferred foreign key violated by a trigger only fails at COMMIT, which is the point being tested.
        keepAlive.Execute("CREATE TABLE sim_parent (id INTEGER PRIMARY KEY); "
            + "CREATE TABLE sim_child (pid INTEGER REFERENCES sim_parent (id) DEFERRABLE INITIALLY DEFERRED); "
            + "CREATE TRIGGER fail_at_commit AFTER UPDATE ON challenges BEGIN INSERT INTO sim_child VALUES (999); END;");

        var failure = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => service.VerifyAsync(scope, code));

        // The classifier only knows the injected marker, so a failed commit must not be turned into "Consumed".
        Assert.DoesNotContain(FaultInjectingDialect.DeadlockMarker, failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, LiveCount(scope));
    }

    // The void is the second UPDATE of the verify transaction: fail any UPDATE that touches a row other than the consumed one.
    private void AddTriggerOnSecondUpdate()
    {
        keepAlive.Execute("DROP TRIGGER IF EXISTS void_deadlock;");
        keepAlive.Execute("CREATE TRIGGER void_deadlock BEFORE UPDATE OF consumed_at ON challenges "
            + "WHEN (SELECT COUNT(*) FROM challenges WHERE consumed_at IS NOT NULL) >= 1 "
            + "BEGIN SELECT RAISE(ABORT, 'deadlock_sim'); END;");
    }
}
