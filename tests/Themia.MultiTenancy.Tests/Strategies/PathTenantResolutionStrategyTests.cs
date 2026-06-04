using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Strategies;

public class PathTenantResolutionStrategyTests
{
    private readonly IOptions<MultiTenancyOptions> _options;

    public PathTenantResolutionStrategyTests()
    {
        _options = Options.Create(new MultiTenancyOptions());
    }

    [Fact]
    public async Task ResolveAsync_WithTenantInFirstSegment_ShouldResolveTenant()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "/acme/api/users", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("path", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithTrailingSlash_ShouldResolveTenant()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "/globex/", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("globex", result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithPathPrefix_ShouldResolveFromSecondSegment()
    {
        var options = Options.Create(new MultiTenancyOptions { PathPrefix = "api" });
        var strategy = new PathTenantResolutionStrategy(options);
        var context = new TenantResolutionContext(null, "/api/acme/users", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("acme", result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithPathPrefix_CaseInsensitive_ShouldResolve()
    {
        var options = Options.Create(new MultiTenancyOptions { PathPrefix = "api" });
        var strategy = new PathTenantResolutionStrategy(options);
        var context = new TenantResolutionContext(null, "/API/acme/users", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("acme", result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithNullPath_ShouldReturnNotFound()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, null, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("path", result.Source);
        Assert.Equal("No path provided", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithEmptyPath_ShouldReturnNotFound()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("No path provided", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithOnlySlash_ShouldReturnNotFound()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "/", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("No segments", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithPrefixButMissingTenantSegment_ShouldReturnNotFound()
    {
        var options = Options.Create(new MultiTenancyOptions { PathPrefix = "api" });
        var strategy = new PathTenantResolutionStrategy(options);
        var context = new TenantResolutionContext(null, "/api", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("Missing tenant segment", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithPrefixMismatch_ShouldReturnNotFound()
    {
        var options = Options.Create(new MultiTenancyOptions { PathPrefix = "api" });
        var strategy = new PathTenantResolutionStrategy(options);
        var context = new TenantResolutionContext(null, "/v1/acme/users", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Equal("Prefix did not match", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "/acme/api", new Dictionary<string, string>(), new Dictionary<string, string>());
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await strategy.ResolveAsync(context, cts.Token);
        });
    }

    [Theory]
    [InlineData("/tenant-with-dashes/api", "tenant-with-dashes")]
    [InlineData("/tenant_with_underscores/api", "tenant_with_underscores")]
    [InlineData("/123-numeric/api", "123-numeric")]
    [InlineData("/UPPERCASE/api", "UPPERCASE")]
    public async Task ResolveAsync_WithVariousTenantFormats_ShouldResolve(string path, string expectedTenant)
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, path, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal(expectedTenant, result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithMultipleSlashes_ShouldNormalizeAndResolve()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "///acme///api///", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("acme", result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithUnicodeInPath_ShouldResolve()
    {
        var strategy = new PathTenantResolutionStrategy(_options);
        var context = new TenantResolutionContext(null, "/テナント/api", new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("テナント", result.Identifier);
    }
}
