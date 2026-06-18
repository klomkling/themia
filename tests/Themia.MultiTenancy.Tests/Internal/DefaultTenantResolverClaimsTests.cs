using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Internal;
using Themia.MultiTenancy.Strategies;
using Themia.MultiTenancy.Tests.TestUtilities;

namespace Themia.MultiTenancy.Tests.Internal;

public class DefaultTenantResolverClaimsTests
{
    [Fact]
    public async Task ResolveAsync_WithClaimsStrategy_ResolvesWithoutConsultingStore()
    {
        var options = Options.Create(new MultiTenancyOptions());
        var claimsStrategy = new ClaimsTenantResolutionStrategy(
            options, NullLogger<ClaimsTenantResolutionStrategy>.Instance);
        // Empty store that records every lookup, so we can prove the store is bypassed
        // (the no-catalog guarantee), not merely that resolution happened to succeed.
        var store = new FakeTenantStore();
        var resolver = new DefaultTenantResolver(
            new ITenantResolutionStrategy[] { claimsStrategy },
            store,
            NullLogger<DefaultTenantResolver>.Instance);

        var context = new TenantResolutionContext(
            null, null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tenant_id"] = "acme" });

        var tenant = await resolver.ResolveAsync(context);

        Assert.NotNull(tenant);
        Assert.Equal("acme", tenant!.Identifier);
        Assert.Equal(0, store.FindCallCount);
    }

    [Fact]
    public async Task ResolveAsync_WithInvalidClaim_ReturnsNullWithoutConsultingStore()
    {
        var options = Options.Create(new MultiTenancyOptions());
        var claimsStrategy = new ClaimsTenantResolutionStrategy(
            options, NullLogger<ClaimsTenantResolutionStrategy>.Instance);
        var store = new FakeTenantStore();
        var resolver = new DefaultTenantResolver(
            new ITenantResolutionStrategy[] { claimsStrategy },
            store,
            NullLogger<DefaultTenantResolver>.Instance);

        // "bad:tenant" is outside the tenant-id charset, so the strategy rejects it (NotFound).
        // A rejected claim must not fall back to a store lookup.
        var context = new TenantResolutionContext(
            null, null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tenant_id"] = "bad:tenant" });

        var tenant = await resolver.ResolveAsync(context);

        Assert.Null(tenant);
        Assert.Equal(0, store.FindCallCount);
    }
}
