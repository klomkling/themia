using System.Collections.Generic;
using System.Linq;
using Themia.Challenges;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

public class SecretGeneratorTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Numeric_ShouldProduceExactlyThatManyDigits(int length)
    {
        var secret = SecretGenerator.Generate(ChallengeFormat.Numeric(length));

        Assert.Equal(length, secret.Length);
        Assert.All(secret, c => Assert.InRange(c, '0', '9'));
    }

    // Leading zeros must survive: a code rendered from an int would turn "004821" into "4821"
    // and the user's six-digit entry would never match.
    [Fact]
    public void Numeric_ShouldPreserveLeadingZeros()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 2000; i++) seen.Add(SecretGenerator.Generate(ChallengeFormat.Numeric(6)));

        Assert.All(seen, s => Assert.Equal(6, s.Length));
    }

    [Fact]
    public void Numeric_ShouldNotRepeatAcrossManyDraws()
    {
        var draws = Enumerable.Range(0, 500).Select(_ => SecretGenerator.Generate(ChallengeFormat.Numeric(6))).ToList();

        // A constant or a low-entropy source shows up immediately as a tiny distinct count.
        Assert.True(draws.Distinct().Count() > 400, $"only {draws.Distinct().Count()} distinct of 500");
    }
}
