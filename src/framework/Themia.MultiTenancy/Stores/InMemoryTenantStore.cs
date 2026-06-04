using System.Collections.Concurrent;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Stores;

/// <summary>
/// Simple in-memory tenant store backed by a concurrent dictionary.
/// </summary>
public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, TenantInfo> _tenants;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryTenantStore"/> class.
    /// </summary>
    public InMemoryTenantStore(IEnumerable<TenantInfo>? seedTenants = null)
    {
        _tenants = new ConcurrentDictionary<string, TenantInfo>(StringComparer.OrdinalIgnoreCase);

        if (seedTenants is not null)
        {
            foreach (var tenant in seedTenants)
            {
                _tenants[tenant.Identifier] = tenant;
            }
        }
    }

    /// <inheritdoc />
    public Task<TenantInfo?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _tenants.TryGetValue(identifier, out var tenant);
        return Task.FromResult<TenantInfo?>(tenant);
    }
}
