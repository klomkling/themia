using Xunit;

namespace Themia.ETax.Withholding.Tests;

// Rounding rule is PENDING accountant confirmation (coord #0164 Q8): half-up to the satang, like VatMath.
public class WithholdingMathTests
{
    [Theory]
    [InlineData(100_000L, 300, 3_000L)] // 1,000.00 @ 3%
    [InlineData(123_456L, 300, 3_704L)] // 37.0368 -> 37.04
    [InlineData(100_050L, 100, 1_001L)] // 10.005 -> 10.01 (half-up; banker's rounding would give 10.00)
    [InlineData(33_333L, 300, 1_000L)] // 9.9999 -> 10.00
    [InlineData(50L, 300, 2L)] // 1.5 satang -> 2
    [InlineData(10L, 300, 0L)] // 0.3 satang -> 0
    [InlineData(100_000L, 150, 1_500L)] // 1.5%
    [InlineData(1_000_000L, 300, 30_000L)] // the #0164 example: 10,000.00 @ 3% = 300.00
    [InlineData(999_999_999L, 1_000, 100_000_000L)] // 99,999,999.9 satang -> 100,000,000
    [InlineData(0L, 300, 0L)]
    [InlineData(100_000L, 0, 0L)]
    [InlineData(100_000L, 10_000, 100_000L)] // 100%
    public void Withhold_ShouldRoundHalfUpToTheSatang(long baseSatang, int rateBasisPoints, long expected) =>
        Assert.Equal(expected, WithholdingMath.Withhold(baseSatang, rateBasisPoints));

    [Fact]
    public void Withhold_ShouldAgreeWithTheDecimalBahtReference_ForEveryBaseAndRate()
    {
        foreach (var rate in new[] { 0, 1, 16, 100, 150, 250, 300, 500, 1_000, 1_500, 10_000 })
        {
            for (long satang = 0; satang <= 5_000; satang++)
            {
                var reference = Math.Round((decimal)satang / 100m * rate / 10_000m, 2, MidpointRounding.AwayFromZero);
                Assert.Equal((long)(reference * 100m), WithholdingMath.Withhold(satang, rate));
            }
        }
    }

    [Fact]
    public void Withhold_ShouldNeverExceedTheBase_AndShouldNotDecreaseAsTheBaseGrows()
    {
        var previous = 0L;
        for (long satang = 0; satang <= 20_000; satang++)
        {
            var withheld = WithholdingMath.Withhold(satang, 3_000);
            Assert.InRange(withheld, previous, satang);
            previous = withheld;
        }
    }

    [Fact]
    public void Withhold_ShouldNotOverflow_AtTheLargestBase() =>
        Assert.Equal(long.MaxValue, WithholdingMath.Withhold(long.MaxValue, WithholdingMath.MaxRateBasisPoints));

    [Theory]
    [InlineData(-1L, 300)]
    [InlineData(100L, -1)]
    [InlineData(100L, 10_001)]
    public void Withhold_ShouldRejectNegativeBasesAndOutOfRangeRates(long baseSatang, int rateBasisPoints) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WithholdingMath.Withhold(baseSatang, rateBasisPoints));
}
