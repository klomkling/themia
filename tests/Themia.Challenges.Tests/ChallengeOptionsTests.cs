using Themia.Challenges;
using Xunit;

namespace Themia.Challenges.Tests;

public class ChallengeOptionsTests
{
    [Fact]
    public void ConfigurePurpose_ShouldRoundTripTheSettings()
    {
        var options = new ChallengeOptions();
        options.ConfigurePurpose("login", p =>
        {
            p.Format = ChallengeFormat.Numeric(6);
            p.Ttl = TimeSpan.FromMinutes(5);
            p.MaxAttempts = 5;
        });

        var purpose = options.GetPurpose("login");

        Assert.Equal(6, purpose.Format.Length);
        Assert.Equal(TimeSpan.FromMinutes(5), purpose.Ttl);
        Assert.Equal(5, purpose.MaxAttempts);
    }

    [Fact]
    public void GetPurpose_ShouldThrow_WhenPurposeWasNeverConfigured()
    {
        var options = new ChallengeOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => options.GetPurpose("login"));

        Assert.Contains("login", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigurePurpose", ex.Message, StringComparison.Ordinal);
    }

    // The mechanism is not removable — only its values are tunable.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxAttempts_ShouldThrow_WhenNotPositive(int value)
    {
        var options = new ChallengeOptions();

        Assert.ThrowsAny<ArgumentException>(() =>
            options.ConfigurePurpose("login", p => p.MaxAttempts = value));
    }

    [Fact]
    public void PerKeyWindow_ShouldThrow_WhenLimitIsNotPositive()
    {
        var options = new ChallengeOptions();

        Assert.ThrowsAny<ArgumentException>(() =>
            options.ConfigurePurpose("login", p => p.PerKeyWindow = (Limit: 0, Window: TimeSpan.FromMinutes(15))));
    }
}
