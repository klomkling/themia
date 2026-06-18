using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Strategies;

/// <summary>
/// Resolves the tenant directly from an authenticated principal's claim (default claim type:
/// <c>tenant_id</c>, configurable via <see cref="MultiTenancyOptions.ClaimType"/>). The claim value
/// <em>is</em> the tenant: a successful resolution returns a fully resolved
/// <see cref="TenantResolutionResult"/> carrying a minimal <see cref="TenantInfo"/> built from the
/// claim, so <c>DefaultTenantResolver</c> returns it directly and no <see cref="ITenantStore"/>
/// catalog lookup is required.
/// </summary>
public sealed class ClaimsTenantResolutionStrategy : ITenantResolutionStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly ILogger<ClaimsTenantResolutionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimsTenantResolutionStrategy"/> class.
    /// </summary>
    public ClaimsTenantResolutionStrategy(IOptions<MultiTenancyOptions> options, ILogger<ClaimsTenantResolutionStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var claimType = _options.Value.ClaimType;

        // Validate the claim through TenantId before trusting it. This path has no ITenantStore
        // round-trip (the store would otherwise implicitly reject unknown ids), so the claim is
        // external input that must satisfy the same tenant-id charset/length the framework keys on.
        if (context.Claims.TryGetValue(claimType, out var claimValue) && TenantId.TryFrom(claimValue, out var tenantId))
        {
            _logger.LogDebug("Tenant {Tenant} resolved from claim {Claim}", tenantId.Value, claimType);
            // The claim is the tenant: return a fully resolved minimal tenant so DefaultTenantResolver
            // returns it directly and bypasses ITenantStore (no catalog required). TenantInfo requires
            // both Id and Identifier, so both take the validated claim value.
            var tenant = new TenantInfo(tenantId.Value, tenantId.Value);
            return Task.FromResult(TenantResolutionResult.Resolved(tenant, claimType));
        }

        return Task.FromResult(TenantResolutionResult.NotFound(claimType, "Claim missing or not a valid tenant identifier"));
    }
}
