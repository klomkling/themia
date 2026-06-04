using Microsoft.Extensions.Logging;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Internal;

internal sealed class DefaultTenantResolver : ITenantResolver
{
    private readonly IEnumerable<ITenantResolutionStrategy> _strategies;
    private readonly ITenantStore _tenantStore;
    private readonly ILogger<DefaultTenantResolver> _logger;

    public DefaultTenantResolver(
        IEnumerable<ITenantResolutionStrategy> strategies,
        ITenantStore tenantStore,
        ILogger<DefaultTenantResolver> logger)
    {
        _strategies = strategies;
        _tenantStore = tenantStore;
        _logger = logger;
    }

    public async Task<TenantInfo?> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var strategy in _strategies)
        {
            var result = await strategy.ResolveAsync(context, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                continue;
            }

            if (result.Tenant is not null)
            {
                _logger.LogDebug("Tenant resolved via {Source}: {TenantId}", result.Source, result.Tenant.Identifier);
                return result.Tenant;
            }

            if (!string.IsNullOrWhiteSpace(result.Identifier))
            {
                var tenant = await _tenantStore.FindByIdentifierAsync(result.Identifier, cancellationToken).ConfigureAwait(false);

                if (tenant is not null)
                {
                    _logger.LogDebug("Tenant {TenantId} loaded from store via {Source}", tenant.Identifier, result.Source ?? "unknown");
                    return tenant;
                }

                // Changed from Warning to Debug - this is expected behavior when tenant is not yet registered
                _logger.LogDebug("Tenant identifier {Identifier} not found in store (source: {Source})", result.Identifier, result.Source ?? "unknown");
            }

        }

        _logger.LogDebug("No tenant resolved");
        return null;
    }
}
