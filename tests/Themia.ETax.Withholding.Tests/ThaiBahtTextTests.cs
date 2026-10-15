using Xunit;

namespace Themia.ETax.Withholding.Tests;

public class ThaiBahtTextTests
{
    [Theory]
    [InlineData(0L, "ศูนย์บาทถ้วน")]
    [InlineData(100L, "หนึ่งบาทถ้วน")]
    [InlineData(1_000L, "สิบบาทถ้วน")]
    [InlineData(1_100L, "สิบเอ็ดบาทถ้วน")]
    [InlineData(2_000L, "ยี่สิบบาทถ้วน")]
    [InlineData(2_100L, "ยี่สิบเอ็ดบาทถ้วน")]
    [InlineData(10_100L, "หนึ่งร้อยเอ็ดบาทถ้วน")]
    [InlineData(30_000L, "สามร้อยบาทถ้วน")]
    [InlineData(100_000_000L, "หนึ่งล้านบาทถ้วน")]
    [InlineData(100_000_100L, "หนึ่งล้านเอ็ดบาทถ้วน")]
    [InlineData(1_100_000_000L, "สิบเอ็ดล้านบาทถ้วน")]
    [InlineData(123_456L, "หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบหกสตางค์")]
    [InlineData(1_000_050L, "หนึ่งหมื่นบาทห้าสิบสตางค์")]
    [InlineData(25L, "ยี่สิบห้าสตางค์")]
    [InlineData(1L, "หนึ่งสตางค์")]
    [InlineData(21L, "ยี่สิบเอ็ดสตางค์")]
    [InlineData(101L, "หนึ่งบาทหนึ่งสตางค์")]
    [InlineData(100_000_000_000L, "หนึ่งพันล้านบาทถ้วน")]
    [InlineData(123_456_789_000_000L, "หนึ่งล้านสองแสนสามหมื่นสี่พันห้าร้อยหกสิบเจ็ดล้านแปดแสนเก้าหมื่นบาทถ้วน")]
    public void Format_ShouldSpellTheAmountInThai(long satang, string expected) =>
        Assert.Equal(expected, ThaiBahtText.Format(satang));

    [Fact]
    public void Format_ShouldHandleTheLargestAmount() =>
        Assert.False(string.IsNullOrEmpty(ThaiBahtText.Format(long.MaxValue)));

    [Fact]
    public void Format_ShouldRejectNegativeAmounts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThaiBahtText.Format(-1));
}
