using Microsoft.Extensions.Options;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Strategies;

/// <summary>
/// Resolves the tenant from the first path segment (e.g., /{tenantId}/api/... ).
/// </summary>
public sealed class PathTenantResolutionStrategy : ITenantResolutionStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathTenantResolutionStrategy"/> class.
    /// </summary>
    public PathTenantResolutionStrategy(IOptions<MultiTenancyOptions> options) => _options = options;

    /// <inheritdoc />
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = context.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(TenantResolutionResult.NotFound("path", "No path provided"));
        }

        // Normalize and split path
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return Task.FromResult(TenantResolutionResult.NotFound("path", "No segments"));
        }

        var prefix = _options.Value.PathPrefix;

        var index = 0;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            if (!segments[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TenantResolutionResult.NotFound("path", "Prefix did not match"));
            }

            index = 1;
        }

        if (segments.Length <= index)
        {
            return Task.FromResult(TenantResolutionResult.NotFound("path", "Missing tenant segment"));
        }

        var tenantIdentifier = segments[index];
        return Task.FromResult(TenantResolutionResult.Identified(tenantIdentifier, "path"));
    }
}
