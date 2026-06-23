using Themia.Modules.Notifications.Outbox;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

public class BackoffPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(1, 2)]     // attempt 1 -> ~2s
    [InlineData(2, 4)]     // attempt 2 -> ~4s
    [InlineData(3, 8)]     // attempt 3 -> ~8s
    public void NextAttempt_grows_exponentially(int attempts, int expectedSeconds)
    {
        var next = BackoffPolicy.NextAttemptAt(Now, attempts);
        Assert.Equal(Now.AddSeconds(expectedSeconds), next);
    }

    [Fact]
    public void NextAttempt_is_capped()
    {
        var next = BackoffPolicy.NextAttemptAt(Now, attempts: 20);
        Assert.True(next - Now <= BackoffPolicy.MaxDelay);
    }

    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void IsDead_when_attempts_reach_cap(int attempts, int max, bool expected)
        => Assert.Equal(expected, BackoffPolicy.IsDead(attempts, max));
}
