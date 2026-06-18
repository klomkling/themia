using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Internal;
using Themia.MultiTenancy.Stores;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Internal;

public class DefaultTenantResolverClaimsTests
{
    [Fact]
    public async Task ResolveAsync_WithClaimsStrategyAndEmptyStore_ResolvesTenant()
    {
        var options = Options.Create(new MultiTenancyOptions());
        var claimsStrategy = new ClaimsTenantResolutionStrategy(
            options, NullLogger<ClaimsTenantResolutionStrategy>.Instance);
        var emptyStore = new InMemoryTenantStore();
        var resolver = new DefaultTenantResolver(
            new ITenantResolutionStrategy[] { claimsStrategy },
            emptyStore,
            NullLogger<DefaultTenantResolver>.Instance);

        var context = new TenantResolutionContext(
            null, null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tenant_id"] = "acme" });

        var tenant = await resolver.ResolveAsync(context);

        Assert.NotNull(tenant);
        Assert.Equal("acme", tenant!.Identifier);
    }
}
