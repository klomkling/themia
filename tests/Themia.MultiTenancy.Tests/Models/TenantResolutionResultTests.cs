using Xunit;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Tests.Models;

public class TenantResolutionResultTests
{
    [Fact]
    public void TenantResolutionResult_NotFound_ShouldCreateFailedResult()
    {
        var result = TenantResolutionResult.NotFound("header", "Header not present");

        Assert.False(result.Success);
        Assert.Null(result.Tenant);
        Assert.Null(result.Identifier);
        Assert.Equal("header", result.Source);
        Assert.Equal("Header not present", result.Reason);
    }

    [Fact]
    public void TenantResolutionResult_NotFound_WithoutReason_ShouldCreateResult()
    {
        var result = TenantResolutionResult.NotFound("path");

        Assert.False(result.Success);
        Assert.Null(result.Tenant);
        Assert.Equal("path", result.Source);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void TenantResolutionResult_Resolved_ShouldCreateSuccessfulResultWithTenant()
    {
        var tenant = new TenantInfo("tenant-1", "acme", "Acme Corp");

        var result = TenantResolutionResult.Resolved(tenant, "header");

        Assert.True(result.Success);
        Assert.Equal(tenant, result.Tenant);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("header", result.Source);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void TenantResolutionResult_Identified_ShouldCreateSuccessfulResultWithIdentifier()
    {
        var result = TenantResolutionResult.Identified("acme", "path");

        Assert.True(result.Success);
        Assert.Null(result.Tenant);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("path", result.Source);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void TenantResolutionResult_Constructor_WithAllParameters_ShouldCreateInstance()
    {
        var tenant = new TenantInfo("tenant-1", "acme");

        var result = new TenantResolutionResult(true, tenant, "acme", "custom", "test reason");

        Assert.True(result.Success);
        Assert.Equal(tenant, result.Tenant);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("custom", result.Source);
        Assert.Equal("test reason", result.Reason);
    }

    [Theory]
    [InlineData("header", "Header missing")]
    [InlineData("path", "Path segment not found")]
    [InlineData("default", "No default configured")]
    public void TenantResolutionResult_NotFound_WithVariousSourcesAndReasons_ShouldCreateResults(string source, string reason)
    {
        var result = TenantResolutionResult.NotFound(source, reason);

        Assert.False(result.Success);
        Assert.Equal(source, result.Source);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void TenantResolutionResult_RecordEquality_WithSameValues_ShouldBeEqual()
    {
        var tenant = new TenantInfo("tenant-1", "acme");

        var result1 = TenantResolutionResult.Resolved(tenant, "header");
        var result2 = TenantResolutionResult.Resolved(tenant, "header");

        Assert.Equal(result2.Success, result1.Success);
        Assert.Equal(result2.Identifier, result1.Identifier);
        Assert.Equal(result2.Source, result1.Source);
    }

    [Fact]
    public void TenantResolutionResult_Success_WithTenant_ShouldHaveIdentifier()
    {
        var tenant = new TenantInfo("tenant-1", "acme", "Acme Corp");

        var result = TenantResolutionResult.Resolved(tenant, "header");

        Assert.True(result.Success);
        Assert.Equal(tenant.Identifier, result.Identifier);
    }

    [Fact]
    public void TenantResolutionResult_Success_WithIdentifierOnly_ShouldNotHaveTenant()
    {
        var result = TenantResolutionResult.Identified("globex", "path");

        Assert.True(result.Success);
        Assert.Equal("globex", result.Identifier);
        Assert.Null(result.Tenant);
    }
}
