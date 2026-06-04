using Xunit;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Tests.Models;

public class TenantInfoTests
{
    [Fact]
    public void TenantInfo_Constructor_WithRequiredFields_ShouldCreateInstance()
    {
        var tenantInfo = new TenantInfo("tenant-1", "acme");

        Assert.Equal("tenant-1", tenantInfo.Id);
        Assert.Equal("acme", tenantInfo.Identifier);
        Assert.Null(tenantInfo.Name);
        Assert.Null(tenantInfo.Environment);
        Assert.Null(tenantInfo.ConnectionString);
        Assert.NotNull(tenantInfo.Properties);
        Assert.Empty(tenantInfo.Properties);
    }

    [Fact]
    public void TenantInfo_Constructor_WithAllFields_ShouldCreateInstance()
    {
        var properties = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };

        var tenantInfo = new TenantInfo(
            "tenant-1",
            "acme",
            "Acme Corp",
            "production",
            "Server=localhost;Database=acme",
            properties);

        Assert.Equal("tenant-1", tenantInfo.Id);
        Assert.Equal("acme", tenantInfo.Identifier);
        Assert.Equal("Acme Corp", tenantInfo.Name);
        Assert.Equal("production", tenantInfo.Environment);
        Assert.Equal("Server=localhost;Database=acme", tenantInfo.ConnectionString);
        Assert.Equal(2, tenantInfo.Properties.Count);
        Assert.Equal("value1", tenantInfo.Properties["key1"]);
        Assert.Equal("value2", tenantInfo.Properties["key2"]);
    }

    [Fact]
    public void TenantInfo_Properties_ShouldBeReadOnly()
    {
        var properties = new Dictionary<string, string> { ["key1"] = "value1" };
        var tenantInfo = new TenantInfo("tenant-1", "acme", Properties: properties);

        // Verify the returned properties dictionary is read-only
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyDictionary<string, string>>(tenantInfo.Properties);
    }

    [Fact]
    public void TenantInfo_WithInitExpression_ShouldOverrideProperties()
    {
        var initialProps = new Dictionary<string, string> { ["key1"] = "value1" };
        var newProps = new Dictionary<string, string> { ["key2"] = "value2" };

        var tenantInfo = new TenantInfo("tenant-1", "acme", Properties: initialProps)
        {
            Properties = newProps
        };

        Assert.Single(tenantInfo.Properties);
        Assert.True(tenantInfo.Properties.ContainsKey("key2"));
        Assert.False(tenantInfo.Properties.ContainsKey("key1"));
    }

    [Fact]
    public void TenantInfo_RecordEquality_ShouldCompareByValue()
    {
        var tenant1 = new TenantInfo("tenant-1", "acme", "Acme Corp");
        var tenant2 = new TenantInfo("tenant-1", "acme", "Acme Corp");

        Assert.Equal(tenant2.Id, tenant1.Id);
        Assert.Equal(tenant2.Identifier, tenant1.Identifier);
        Assert.Equal(tenant2.Name, tenant1.Name);
    }

    [Fact]
    public void TenantInfo_RecordEquality_WithDifferentValues_ShouldNotBeEqual()
    {
        var tenant1 = new TenantInfo("tenant-1", "acme", "Acme Corp");
        var tenant2 = new TenantInfo("tenant-2", "acme", "Acme Corp");

        Assert.NotEqual(tenant2, tenant1);
        Assert.False(tenant1 == tenant2);
    }

    [Theory]
    [InlineData("tenant-1", "acme")]
    [InlineData("tenant-2", "globex")]
    [InlineData("tenant-3", "initech")]
    public void TenantInfo_Constructor_WithVariousIdentifiers_ShouldCreateInstances(string id, string identifier)
    {
        var tenantInfo = new TenantInfo(id, identifier);

        Assert.Equal(id, tenantInfo.Id);
        Assert.Equal(identifier, tenantInfo.Identifier);
    }

    [Fact]
    public void TenantInfo_ToString_ShouldNotLeakConnectionString()
    {
        var tenantInfo = new TenantInfo(
            "tenant-1",
            "acme",
            "Acme Corp",
            "production",
            "Server=localhost;Database=acme;User=sa;Password=Sup3rSecret!");

        var text = tenantInfo.ToString();

        Assert.DoesNotContain("Sup3rSecret!", text);
        Assert.DoesNotContain("Server=localhost", text);
        Assert.Contains("ConnectionString = ***", text);
        // Non-secret fields remain visible for diagnostics.
        Assert.Contains("acme", text);
    }

    [Fact]
    public void TenantInfo_JsonSerialize_ShouldNotLeakConnectionString()
    {
        var tenantInfo = new TenantInfo(
            "tenant-1",
            "acme",
            ConnectionString: "Server=localhost;Database=acme;User=sa;Password=Sup3rSecret!");

        var json = System.Text.Json.JsonSerializer.Serialize(tenantInfo);

        Assert.DoesNotContain("Sup3rSecret!", json);
        Assert.DoesNotContain("ConnectionString", json);
    }
}
