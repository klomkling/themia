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
        Assert.Equal("Claim not present", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithBlankClaim_ReturnsNotFound()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "   " });

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
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
