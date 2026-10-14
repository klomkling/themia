using Xunit;

namespace Themia.ETax.Tests;

public class ThaiTaxIdTests
{
    [Theory]
    [InlineData("0105536078673")]
    [InlineData("1101700230708")]
    [InlineData("1234567890121")]
    public void HasValidChecksum_ShouldBeTrue_ForValidMod11Ids(string id)
    {
        Assert.True(ThaiTaxId.IsWellFormed(id));
        Assert.True(ThaiTaxId.HasValidChecksum(id));
    }

    [Theory]
    [InlineData("0105536078670")]
    [InlineData("1234567890123")]
    public void HasValidChecksum_ShouldBeFalse_WhenCheckDigitIsWrong(string id)
    {
        Assert.True(ThaiTaxId.IsWellFormed(id));
        Assert.False(ThaiTaxId.HasValidChecksum(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("010553607867")]
    [InlineData("01055360786733")]
    [InlineData("010553607867a")]
    [InlineData("๐๑๐๕๕๓๖๐๗๘๖๗๓")]
    public void IsWellFormed_ShouldBeFalse_ForNonThirteenAsciiDigits(string? id)
    {
        Assert.False(ThaiTaxId.IsWellFormed(id));
        Assert.False(ThaiTaxId.HasValidChecksum(id));
    }

    [Theory]
    [InlineData("00000", true)]
    [InlineData("00123", true)]
    [InlineData("0000", false)]
    [InlineData("000000", false)]
    [InlineData("0000a", false)]
    [InlineData(null, false)]
    public void ThaiBranchCode_IsWellFormed_ShouldRequireFiveDigits(string? branch, bool expected)
    {
        Assert.Equal(expected, ThaiBranchCode.IsWellFormed(branch));
    }
}
