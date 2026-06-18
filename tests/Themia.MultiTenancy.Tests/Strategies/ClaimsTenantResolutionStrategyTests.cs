using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Strategies;

public class ClaimsTenantResolutionStrategyTests
{
    private readonly IOptions<MultiTenancyOptions> _options = Options.Create(new MultiTenancyOptions());
    private readonly NullLogger<ClaimsTenantResolutionStrategy> _logger = NullLogger<ClaimsTenantResolutionStrategy>.Instance;

    private static TenantResolutionContext ContextWithClaims(Dictionary<string, string> claims) =>
        new(null, null, new Dictionary<string, string>(), claims);

    [Fact]
    public async Task ResolveAsync_WithDefaultClaim_ReturnsResolvedTenant()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "acme" });

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.NotNull(result.Tenant);
        Assert.Equal("acme", result.Tenant!.Id);
        Assert.Equal("acme", result.Tenant.Identifier);
        Assert.Equal("tenant_id", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithCustomClaimType_ReturnsResolvedTenant()
    {
        var options = Options.Create(new MultiTenancyOptions { ClaimType = "tid" });
        var strategy = new ClaimsTenantResolutionStrategy(options, _logger);
        var context = ContextWithClaims(new() { ["tid"] = "globex" });

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("globex", result.Tenant!.Identifier);
        Assert.Equal("tid", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithoutClaim_ReturnsNotFound()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Null(result.Tenant);
        Assert.Equal("tenant_id", result.Source);
        Assert.Equal("Claim missing or not a valid tenant identifier", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithBlankClaim_ReturnsNotFound()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "   " });

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("bad:tenant")]   // colon is outside the tenant-id charset
    [InlineData("a/b")]          // path separator
    [InlineData("has space")]
    public async Task ResolveAsync_WithInvalidClaimValue_ReturnsNotFound(string claimValue)
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = claimValue });

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Null(result.Tenant);
    }

    [Fact]
    public async Task ResolveAsync_WithGuidClaim_ResolvesTenant()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "0f8fad5b-d9cb-469f-a165-70867728950e" });

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", result.Tenant!.Identifier);
    }

    [Fact]
    public async Task ResolveAsync_WithCancellation_Throws()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "acme" });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => strategy.ResolveAsync(context, cts.Token));
    }
}
