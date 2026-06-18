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

    [Theory]
    [InlineData(123)]
    [InlineData(-7)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void FromInt_RoundTripsThroughAsInt32(int value)
    {
        Assert.Equal(value, TenantId.From(value).AsInt32());
    }

    [Theory]
    [InlineData(123L)]
    [InlineData(-7L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void FromLong_RoundTripsThroughAsInt64(long value)
    {
        Assert.Equal(value, TenantId.From(value).AsInt64());
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
    public void AsInt64_Throws_ForNonIntegerValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("not-a-number").AsInt64());
    }

    [Fact]
    public void AsGuid_Throws_ForNonGuidValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("acme").AsGuid());
    }

    [Fact]
    public void AsGuid_Throws_ForNonHyphenatedGuid()
    {
        // "N" format (32 hex digits, no hyphens) passes the TenantId charset but is not the
        // canonical "D" encoding From(Guid) produces, so AsGuid must reject it.
        Assert.Throws<FormatException>(() => new TenantId("0f8fad5bd9cb469fa16570867728950e").AsGuid());
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
    public void TryAsInt64_ReturnsTrue_ForValueBeyondInt32()
    {
        Assert.True(new TenantId("9223372036854775807").TryAsInt64(out var value));
        Assert.Equal(long.MaxValue, value);
    }

    [Fact]
    public void TryAsInt64_ReturnsFalse_ForNonIntegerValue()
    {
        Assert.False(new TenantId("acme").TryAsInt64(out var value));
        Assert.Equal(0L, value);
    }

    [Fact]
    public void TryAsGuid_ReturnsFalse_ForNonGuidValue()
    {
        Assert.False(new TenantId("acme").TryAsGuid(out _));
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("tenant-1")]
    [InlineData("tenant_2")]
    [InlineData("42")]
    public void TryFrom_ReturnsTrue_ForValidValue(string value)
    {
        Assert.True(TenantId.TryFrom(value, out var tenantId));
        Assert.Equal(value, tenantId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad:tenant")]
    [InlineData("a/b")]
    [InlineData("has space")]
    public void TryFrom_ReturnsFalse_ForInvalidValue(string? value)
    {
        Assert.False(TenantId.TryFrom(value, out var tenantId));
        Assert.Equal(default, tenantId);
    }

    [Fact]
    public void TryFrom_ReturnsFalse_ForOverlongValue()
    {
        var tooLong = new string('a', TenantId.MaxLength + 1);

        Assert.False(TenantId.TryFrom(tooLong, out _));
    }
}
