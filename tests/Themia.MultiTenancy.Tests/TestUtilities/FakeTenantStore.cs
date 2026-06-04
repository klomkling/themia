using System.Collections.Concurrent;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Tests.TestUtilities;

/// <summary>
/// Fake tenant store for testing that tracks invocations without using reflection.
/// </summary>
public sealed class FakeTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, TenantInfo> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private int _findCallCount;

    public int FindCallCount => _findCallCount;

    public FakeTenantStore(params TenantInfo[] tenants)
    {
        foreach (var tenant in tenants)
        {
            _tenants[tenant.Identifier] = tenant;
        }
    }

    public Task<TenantInfo?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _findCallCount);
        cancellationToken.ThrowIfCancellationRequested();

        _tenants.TryGetValue(identifier, out var tenant);
        return Task.FromResult<TenantInfo?>(tenant);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _findCallCount, 0);
    }
}
