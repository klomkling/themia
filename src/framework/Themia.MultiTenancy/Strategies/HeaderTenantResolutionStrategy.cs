using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Strategies;

/// <summary>
/// Resolves the tenant from a header (default: X-Tenant-ID).
/// </summary>
public sealed class HeaderTenantResolutionStrategy : ITenantResolutionStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly ILogger<HeaderTenantResolutionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderTenantResolutionStrategy"/> class.
    /// </summary>
    public HeaderTenantResolutionStrategy(IOptions<MultiTenancyOptions> options, ILogger<HeaderTenantResolutionStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var headerName = _options.Value.HeaderName;

        if (context.Headers.TryGetValue(headerName, out var tenantIdentifier) && !string.IsNullOrWhiteSpace(tenantIdentifier))
        {
            _logger.LogDebug("Tenant identifier {Tenant} found in header {Header}", tenantIdentifier, headerName);
            return Task.FromResult(TenantResolutionResult.Identified(tenantIdentifier, headerName));
        }

        return Task.FromResult(TenantResolutionResult.NotFound(headerName, "Header not present"));
    }
}
