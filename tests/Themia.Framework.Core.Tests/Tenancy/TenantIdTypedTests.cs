using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Core.Tests.Tenancy;

public class TenantIdTypedTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(-7, "-7")]
    [InlineData(int.MaxValue, "2147483647")]
    public void FromInt_EncodesAsInvariantDecimal(int value, string expected)
    {
        Assert.Equal(expected, TenantId.From(value).Value);
    }

    [Fact]
    public void FromLong_EncodesAsInvariantDecimal()
    {
        Assert.Equal("9223372036854775807", TenantId.From(long.MaxValue).Value);
    }

    [Fact]
    public void FromGuid_EncodesAsLowercaseHyphenatedHex()
    {
        var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", TenantId.From(guid).Value);
    }

    [Fact]
    public void FromInt_RoundTripsThroughAsInt32()
    {
        Assert.Equal(123, TenantId.From(123).AsInt32());
    }

    [Fact]
    public void FromLong_RoundTripsThroughAsInt64()
    {
        Assert.Equal(123L, TenantId.From(123L).AsInt64());
    }

    [Fact]
    public void FromGuid_RoundTripsThroughAsGuid()
    {
        var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal(guid, TenantId.From(guid).AsGuid());
    }

    [Fact]
    public void AsInt32_Throws_ForNonIntegerValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("not-a-number").AsInt32());
    }

    [Fact]
    public void AsGuid_Throws_ForNonGuidValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("acme").AsGuid());
    }

    [Fact]
    public void TryAsInt32_ReturnsTrue_ForIntegerValue()
    {
        Assert.True(new TenantId("55").TryAsInt32(out var value));
        Assert.Equal(55, value);
    }

    [Fact]
    public void TryAsInt32_ReturnsFalse_ForNonIntegerValue()
    {
        Assert.False(new TenantId("acme").TryAsInt32(out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryAsGuid_ReturnsFalse_ForNonGuidValue()
    {
        Assert.False(new TenantId("acme").TryAsGuid(out _));
    }
}
