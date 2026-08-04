using Themia.Challenges;
using Xunit;

namespace Themia.Challenges.Tests;

public class ChallengeScopeTests
{
    [Theory]
    [InlineData("+15551234567")]
    [InlineData("someone@example.com")]
    public void ToString_ShouldNotExposeTheFullKey(string key)
    {
        var scope = new ChallengeScope(key, "login", "tenant-1");

        var rendered = scope.ToString();

        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);
        Assert.Contains("login", rendered, StringComparison.Ordinal);
        Assert.Contains("tenant-1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ShouldKeepLastFourCharactersOfTheKeyForCorrelation()
    {
        var scope = new ChallengeScope("+15551234567", "login");

        var rendered = scope.ToString();

        Assert.Contains("4567", rendered, StringComparison.Ordinal);
    }
}
