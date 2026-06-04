using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Strategies;

public class HeaderTenantResolutionStrategyTests
{
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly NullLogger<HeaderTenantResolutionStrategy> _logger;

    public HeaderTenantResolutionStrategyTests()
    {
        _options = Options.Create(new MultiTenancyOptions());
        _logger = NullLogger<HeaderTenantResolutionStrategy>.Instance;
    }

    [Fact]
    public async Task ResolveAsync_WithDefaultHeader_ShouldResolveTenant()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "acme" };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("X-Tenant-ID", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithCustomHeaderName_ShouldResolveTenant()
    {
        var options = Options.Create(new MultiTenancyOptions { HeaderName = "X-Custom-Tenant" });
        var strategy = new HeaderTenantResolutionStrategy(options, _logger);
        var headers = new Dictionary<string, string> { ["X-Custom-Tenant"] = "globex" };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("globex", result.Identifier);
        Assert.Equal("X-Custom-Tenant", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithoutHeader_ShouldReturnNotFound()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string>();
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Null(result.Identifier);
        Assert.Equal("X-Tenant-ID", result.Source);
        Assert.Equal("Header not present", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithEmptyHeaderValue_ShouldReturnNotFound()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "" };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Null(result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithWhitespaceHeaderValue_ShouldReturnNotFound()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "   " };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ResolveAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "acme" };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());
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
    [InlineData("initech")]
    [InlineData("tenant-with-dashes")]
    [InlineData("tenant_with_underscores")]
    public async Task ResolveAsync_WithVariousTenantIdentifiers_ShouldResolve(string tenantId)
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = tenantId };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal(tenantId, result.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithUnicodeIdentifier_ShouldResolve()
    {
        var strategy = new HeaderTenantResolutionStrategy(_options, _logger);
        var headers = new Dictionary<string, string> { ["X-Tenant-ID"] = "テナント-🏢" };
        var context = new TenantResolutionContext(null, null, headers, new Dictionary<string, string>());

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("テナント-🏢", result.Identifier);
    }
}
