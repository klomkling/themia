using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Core.Tests.Tenancy;

public class TenantIdTests
{
    [Fact]
    public void From_ReturnsNull_ForEmpty()
    {
        Assert.Null(TenantId.From(string.Empty));
    }

    [Fact]
    public void Constructor_RejectsEmptyValues()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(" "));
    }

    [Fact]
    public void Value_RoundTrips()
    {
        var tenantId = new TenantId("tenant-1");

        Assert.Equal("tenant-1", tenantId.Value);
        Assert.Equal("tenant-1", tenantId.ToString());
    }
}
