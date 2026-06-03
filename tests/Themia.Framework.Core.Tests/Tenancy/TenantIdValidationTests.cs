using Xunit;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Framework.Core.Tests.Tenancy;

public class TenantIdValidationTests
{
    [Theory]
    [InlineData("tenant-123")]
    [InlineData("tenant_123")]
    [InlineData("TENANT123")]
    [InlineData("t")]
    [InlineData("abc-def_123")]
    public void Constructor_ValidTenantId_Succeeds(string value)
    {
        var tenantId = new TenantId(value);

        Assert.Equal(value, tenantId.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Constructor_EmptyOrWhitespace_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => new TenantId(value));
    }

    [Fact]
    public void Constructor_TooLong_ThrowsArgumentException()
    {
        var longValue = new string('a', TenantId.MaxLength + 1);

        var exception = Assert.Throws<ArgumentException>(() => new TenantId(longValue));
        Assert.Contains("exceed", exception.Message);
    }

    [Theory]
    [InlineData("tenant@123")]
    [InlineData("tenant#123")]
    [InlineData("tenant.123")]
    [InlineData("tenant 123")]
    [InlineData("tenant/123")]
    [InlineData("tenant\\123")]
    public void Constructor_InvalidCharacters_ThrowsArgumentException(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => new TenantId(value));
        Assert.Contains("alphanumeric", exception.Message);
    }

    [Fact]
    public void From_ValidValue_ReturnsTenantId()
    {
        var tenantId = TenantId.From("tenant-123");

        Assert.NotNull(tenantId);
        Assert.Equal("tenant-123", tenantId.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_NullOrWhitespace_ReturnsNull(string? value)
    {
        var tenantId = TenantId.From(value);

        Assert.Null(tenantId);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var tenantId = new TenantId("tenant-123");

        Assert.Equal("tenant-123", tenantId.ToString());
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var tenant1 = new TenantId("tenant-123");
        var tenant2 = new TenantId("tenant-123");

        Assert.Equal(tenant1, tenant2);
    }

    [Fact]
    public void Equality_DifferentValue_AreNotEqual()
    {
        var tenant1 = new TenantId("tenant-123");
        var tenant2 = new TenantId("tenant-456");

        Assert.NotEqual(tenant1, tenant2);
    }
}
