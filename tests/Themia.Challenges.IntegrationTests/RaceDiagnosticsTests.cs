using System.Threading;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// The diagnostics of the forced-overlap harness (<see cref="RaceOutcome"/> and the timeout message of
/// <see cref="SiblingGateDialect"/>). They need no database, so they run with the ordinary tests.
/// </summary>
public sealed class RaceDiagnosticsTests
{
    [Fact]
    public async Task WhenAllReported_ShouldReturnEveryResult_WhenNoRacerFails()
    {
        var results = await RaceOutcome.WhenAllReportedAsync(Task.FromResult(1), Task.FromResult(2));

        Assert.Equal([1, 2], results);
    }

    [Fact]
    public async Task WhenAllReported_ShouldNameBothRacers_WhenOneFailsAndOneCompletes()
    {
        var failing = Task.FromException<int>(new InvalidOperationException("gate timed out"));

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => RaceOutcome.WhenAllReportedAsync(failing, Task.FromResult(7)));

        Assert.Contains("racer 0: failed with InvalidOperationException: gate timed out", thrown.Message);
        Assert.Contains("racer 1: completed with 7", thrown.Message);
        Assert.Single(thrown.InnerExceptions);
    }

    [Fact]
    public async Task WhenAllReported_ShouldKeepBothExceptions_WhenBothRacersFail()
    {
        var first = Task.FromException<int>(new InvalidOperationException("first"));
        var second = Task.FromException<int>(new TimeoutException("second"));

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => RaceOutcome.WhenAllReportedAsync(first, second));

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Contains("racer 0: failed", thrown.Message);
        Assert.Contains("racer 1: failed with TimeoutException: second", thrown.Message);
    }

    [Fact]
    public void GateTimeoutMessage_ShouldSayWhoHadNotArrived_AndTheThreadPoolState()
    {
        using var barrier = new Barrier(2);

        var message = SiblingGateDialect.Diagnose(barrier, "ConsumeSql");

        Assert.Contains("never both reached ConsumeSql within 60 s", message);
        Assert.Contains("Parties not yet arrived: 2 of 2", message);
        Assert.Contains("Thread pool:", message);
        Assert.Contains("queued work items", message);
    }
}
