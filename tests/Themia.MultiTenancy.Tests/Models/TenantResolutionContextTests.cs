using Xunit;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Tests.Models;

public class TenantResolutionContextTests
{
    [Fact]
    public void TenantResolutionContext_Constructor_WithAllParameters_ShouldCreateInstance()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "acme" };
        var claims = new Dictionary<string, string> { ["sub"] = "user123" };

        var context = new TenantResolutionContext("localhost", "/api/test", headers, claims);

        Assert.Equal("localhost", context.Host);
        Assert.Equal("/api/test", context.Path);
        Assert.Equal(headers, context.Headers);
        Assert.Equal(claims, context.Claims);
    }

    [Fact]
    public void TenantResolutionContext_Constructor_WithNullValues_ShouldCreateInstance()
    {
        var headers = new Dictionary<string, string>();
        var claims = new Dictionary<string, string>();

        var context = new TenantResolutionContext(null, null, headers, claims);

        Assert.Null(context.Host);
        Assert.Null(context.Path);
        Assert.NotNull(context.Headers);
        Assert.NotNull(context.Claims);
    }

    [Fact]
    public void TenantResolutionContext_Empty_ShouldProvideEmptyContext()
    {
        var context = TenantResolutionContext.Empty;

        Assert.Null(context.Host);
        Assert.Null(context.Path);
        Assert.NotNull(context.Headers);
        Assert.Empty(context.Headers);
        Assert.NotNull(context.Claims);
        Assert.Empty(context.Claims);
    }

    [Fact]
    public void TenantResolutionContext_RecordEquality_WithSameValues_ShouldBeEqual()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "acme" };
        var claims = new Dictionary<string, string> { ["sub"] = "user123" };

        var context1 = new TenantResolutionContext("localhost", "/api/test", headers, claims);
        var context2 = new TenantResolutionContext("localhost", "/api/test", headers, claims);

        Assert.Equal(context2.Host, context1.Host);
        Assert.Equal(context2.Path, context1.Path);
    }

    [Theory]
    [InlineData("example.com", "/tenant1/api")]
    [InlineData("localhost", "/api/v1")]
    [InlineData("subdomain.example.com", "/path/to/resource")]
    public void TenantResolutionContext_WithVariousHostsAndPaths_ShouldCreateInstances(string host, string path)
    {
        var headers = new Dictionary<string, string>();
        var claims = new Dictionary<string, string>();

        var context = new TenantResolutionContext(host, path, headers, claims);

        Assert.Equal(host, context.Host);
        Assert.Equal(path, context.Path);
    }

    [Fact]
    public void TenantResolutionContext_WithMultipleHeaders_ShouldPreserveAllHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Tenant-ID"] = "acme",
            ["Authorization"] = "Bearer token",
            ["Content-Type"] = "application/json"
        };
        var claims = new Dictionary<string, string>();

        var context = new TenantResolutionContext("localhost", "/api", headers, claims);

        Assert.Equal(3, context.Headers.Count);
        Assert.Equal("acme", context.Headers["X-Tenant-ID"]);
        Assert.Equal("Bearer token", context.Headers["Authorization"]);
        Assert.Equal("application/json", context.Headers["Content-Type"]);
    }

    [Fact]
    public void TenantResolutionContext_WithMultipleClaims_ShouldPreserveAllClaims()
    {
        var headers = new Dictionary<string, string>();
        var claims = new Dictionary<string, string>
        {
            ["sub"] = "user123",
            ["email"] = "user@example.com",
            ["role"] = "admin"
        };

        var context = new TenantResolutionContext("localhost", "/api", headers, claims);

        Assert.Equal(3, context.Claims.Count);
        Assert.Equal("user123", context.Claims["sub"]);
        Assert.Equal("user@example.com", context.Claims["email"]);
        Assert.Equal("admin", context.Claims["role"]);
    }
}
