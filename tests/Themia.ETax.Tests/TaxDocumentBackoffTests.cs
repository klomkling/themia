using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Max = TimeSpan.FromHours(1);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void Delay_ShouldDoubleEachAttempt_WhenJitterIsCentred(int attempt, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, TaxDocumentBackoff.Delay(attempt, Initial, Max, 0.5).TotalSeconds, precision: 3);

    [Fact]
    public void Delay_ShouldStayWithinTwentyPercentOfTheBase()
    {
        Assert.Equal(24, TaxDocumentBackoff.Delay(1, Initial, Max, 0).TotalSeconds, precision: 3);
        Assert.Equal(36, TaxDocumentBackoff.Delay(1, Initial, Max, 1).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Delay_ShouldNeverExceedTheCap_EvenForHugeAttemptNumbers()
    {
        Assert.Equal(Max, TaxDocumentBackoff.Delay(40, Initial, Max, 1));
        Assert.Equal(Max, TaxDocumentBackoff.Delay(int.MaxValue, Initial, Max, 1));
    }
}
