using Microsoft.Extensions.Options;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Strategies;

/// <summary>
/// Falls back to a configured default tenant identifier when no other strategy succeeds.
/// </summary>
public sealed class DefaultTenantResolutionStrategy : ITenantResolutionStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultTenantResolutionStrategy"/> class.
    /// </summary>
    public DefaultTenantResolutionStrategy(IOptions<MultiTenancyOptions> options) => _options = options;

    /// <inheritdoc />
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaultTenant = _options.Value.DefaultTenantIdentifier;

        if (string.IsNullOrWhiteSpace(defaultTenant))
        {
            return Task.FromResult(TenantResolutionResult.NotFound("default", "No default tenant configured"));
        }

        return Task.FromResult(TenantResolutionResult.Identified(defaultTenant, "default"));
    }
}
