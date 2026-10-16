using Xunit;

namespace Themia.ETax.Withholding.Tests;

// The Revenue Department's rule (coord #0164 [9]): the tax rounds UP to the satang, looking at every digit after the second decimal of baht
// (ceiling). It is not VatMath's half-up.
public class WithholdingMathTests
{
    [Theory]
    [InlineData(100_000L, 300, 3_000L)] // 1,000.00 @ 3% = 30.00, exact
    [InlineData(123_456L, 300, 3_704L)] // 1,234.56 @ 3% = 37.0368 -> 37.04
    [InlineData(100_050L, 100, 1_001L)] // 1,000.50 @ 1% = 10.005 -> 10.01
    [InlineData(33_333L, 300, 1_000L)] // 333.33 @ 3% = 9.9999 -> 10.00
    [InlineData(50L, 300, 2L)] // 0.50 @ 3% = 0.015 -> 0.02
    [InlineData(10L, 300, 1L)] // 0.10 @ 3% = 0.003 -> 0.01 (half-up would give 0.00)
    [InlineData(1L, 1, 1L)] // 0.01 @ 0.01% = 0.000001 -> 0.01
    [InlineData(100_000L, 150, 1_500L)] // 1.5%, exact
    [InlineData(1_000_000L, 300, 30_000L)] // 10,000.00 @ 3% = 300.00, exact
    [InlineData(999_999_999L, 1_000, 100_000_000L)] // 99,999,999.9 satang -> 100,000,000
    [InlineData(0L, 300, 0L)]
    [InlineData(100_000L, 0, 0L)]
    [InlineData(100_000L, 10_000, 100_000L)] // 100%
    public void Withhold_ShouldRoundUpToTheSatang(long baseSatang, int rateBasisPoints, long expected) =>
        Assert.Equal(expected, WithholdingMath.Withhold(baseSatang, rateBasisPoints));

    // The exact tax is 1011.5, 1011.4 and 1011.01 satang (10.115, 10.114, 10.1101 baht): every one rounds UP to 10.12.
    // An exact 10.11 stays 10.11.
    [Theory]
    [InlineData(10_115L, 1_000, 1_012L)] // 101.15 baht @ 10% = 10.115
    [InlineData(10_114L, 1_000, 1_012L)] // 101.14 baht @ 10% = 10.114
    [InlineData(101_101L, 100, 1_012L)] // 1,011.01 baht @ 1% = 10.1101
    [InlineData(20_230L, 500, 1_012L)] // 202.30 baht @ 5% = 10.115
    [InlineData(20_228L, 500, 1_012L)] // 202.28 baht @ 5% = 10.114
    [InlineData(101_100L, 100, 1_011L)] // 1,011.00 baht @ 1% = 10.11 exactly
    [InlineData(10_110L, 1_000, 1_011L)] // 101.10 baht @ 10% = 10.11 exactly
    public void Withhold_ShouldLookAtEveryDigitAfterTheSecondDecimal_AndLeaveAnExactAmountAlone(long baseSatang, int rateBasisPoints, long expectedSatang)
    {
        Assert.Equal(expectedSatang, WithholdingMath.Withhold(baseSatang, rateBasisPoints));
        Assert.Equal(
            decimal.Ceiling(baseSatang / 100m * rateBasisPoints / 10_000m * 100m) / 100m,
            WithholdingMath.Withhold(baseSatang, rateBasisPoints) / 100m);
    }

    [Fact]
    public void Withhold_ShouldAgreeWithTheDecimalBahtCeilingReference_ForEveryBaseAndRate()
    {
        foreach (var rate in new[] { 0, 1, 16, 100, 150, 250, 300, 500, 1_000, 1_500, 10_000 })
        {
            for (long satang = 0; satang <= 5_000; satang++)
            {
                var exactBaht = satang / 100m * rate / 10_000m;
                var reference = decimal.Ceiling(exactBaht * 100m) / 100m;
                Assert.Equal((long)(reference * 100m), WithholdingMath.Withhold(satang, rate));
            }
        }
    }

    [Fact]
    public void Withhold_ShouldBeTheSmallestWholeSatangThatCoversTheExactTax()
    {
        foreach (var rate in new[] { 1, 7, 300, 1_000, 9_999 })
        {
            for (long satang = 0; satang <= 3_000; satang++)
            {
                var withheld = WithholdingMath.Withhold(satang, rate);
                Assert.True(withheld * 10_000 >= satang * rate, $"{satang} @ {rate}: {withheld} is below the exact tax");
                Assert.True(withheld == 0 || (withheld - 1) * 10_000 < satang * rate, $"{satang} @ {rate}: {withheld} is more than one satang above the exact tax");
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
    public void Withhold_ShouldNotOverflow_AtTheLargestBase()
    {
        Assert.Equal(long.MaxValue, WithholdingMath.Withhold(long.MaxValue, WithholdingMath.MaxRateBasisPoints));
        Assert.InRange(WithholdingMath.Withhold(long.MaxValue, 9_999), 0, long.MaxValue);
    }

    [Theory]
    [InlineData(-1L, 300)]
    [InlineData(100L, -1)]
    [InlineData(100L, 10_001)]
    public void Withhold_ShouldRejectNegativeBasesAndOutOfRangeRates(long baseSatang, int rateBasisPoints) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WithholdingMath.Withhold(baseSatang, rateBasisPoints));
}
