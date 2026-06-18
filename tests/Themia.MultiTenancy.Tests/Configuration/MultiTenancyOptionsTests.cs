using Xunit;
using Themia.MultiTenancy;

namespace Themia.MultiTenancy.Tests.Configuration;

public class MultiTenancyOptionsTests
{
    [Fact]
    public void MultiTenancyOptions_DefaultValues_ShouldBeSet()
    {
        var options = new MultiTenancyOptions();

        Assert.Equal("X-Tenant-ID", options.HeaderName);
        Assert.Null(options.PathPrefix);
        Assert.Null(options.DefaultTenantIdentifier);
        Assert.True(options.UseDefaultStrategies);
    }

    [Fact]
    public void MultiTenancyOptions_SetHeaderName_ShouldUpdate()
    {
        var options = new MultiTenancyOptions { HeaderName = "X-Custom-Tenant" };

        Assert.Equal("X-Custom-Tenant", options.HeaderName);
    }

    [Fact]
    public void MultiTenancyOptions_SetPathPrefix_ShouldUpdate()
    {
        var options = new MultiTenancyOptions { PathPrefix = "api" };

        Assert.Equal("api", options.PathPrefix);
    }

    [Fact]
    public void MultiTenancyOptions_SetDefaultTenant_ShouldUpdate()
    {
        var options = new MultiTenancyOptions { DefaultTenantIdentifier = "default" };

        Assert.Equal("default", options.DefaultTenantIdentifier);
    }

    [Fact]
    public void MultiTenancyOptions_SetUseDefaultStrategies_ShouldUpdate()
    {
        var options = new MultiTenancyOptions { UseDefaultStrategies = false };

        Assert.False(options.UseDefaultStrategies);
    }

    [Fact]
    public void ClaimType_DefaultsToTenantId()
    {
        var options = new MultiTenancyOptions();

        Assert.Equal("tenant_id", options.ClaimType);
    }

    [Fact]
    public void ClaimType_IsSettable()
    {
        var options = new MultiTenancyOptions { ClaimType = "tid" };

        Assert.Equal("tid", options.ClaimType);
    }
}
