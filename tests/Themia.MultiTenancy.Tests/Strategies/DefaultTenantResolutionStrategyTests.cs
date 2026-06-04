using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Strategies;

public class DefaultTenantResolutionStrategyTests
{
    [Fact]
    public async Task ResolveAsync_WithConfiguredDefault_ShouldResolveToDefault()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = "default-tenant" });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("default-tenant", result.Identifier);
        Assert.Equal("default", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithoutConfiguredDefault_ShouldReturnNotFound()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = null });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("default", result.Source);
        Assert.Equal("No default tenant configured", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithEmptyDefault_ShouldReturnNotFound()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = "" });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("No default tenant configured", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithWhitespaceDefault_ShouldReturnNotFound()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = "   " });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ResolveAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = "default-tenant" });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await strategy.ResolveAsync(context, cts.Token);
        });
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("globex")]
    [InlineData("tenant-123")]
    [InlineData("UPPERCASE")]
    public async Task ResolveAsync_WithVariousDefaultTenants_ShouldResolve(string defaultTenant)
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = defaultTenant });
        var strategy = new DefaultTenantResolutionStrategy(options);
        var context = TenantResolutionContext.Empty;

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal(defaultTenant, result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_IgnoresContextValues_AlwaysReturnsDefault()
    {
        var options = Options.Create(new MultiTenancyOptions { DefaultTenantIdentifier = "default-tenant" });
        var strategy = new DefaultTenantResolutionStrategy(options);

        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "other-tenant" };
        var context = new TenantResolutionContext("localhost", "/other/path", headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("default-tenant", result.Identifier);
    }
}
