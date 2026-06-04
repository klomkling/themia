using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Internal;

/// <summary>
/// Internal implementation of <see cref="ITenantAccessor"/> and <see cref="ITenantSetter"/>
/// that holds the current tenant for the request scope.
/// Registered as a single Scoped instance exposed via both interfaces so that:
/// <list type="bullet">
///   <item>Application code receives <see cref="ITenantAccessor"/> (read-only).</item>
///   <item>Resolution middleware receives <see cref="ITenantSetter"/> (write-only).</item>
/// </list>
/// </summary>
internal sealed class TenantAccessor : ITenantAccessor, ITenantSetter
{
    private TenantInfo? _current;

    /// <inheritdoc />
    public TenantInfo? Current => _current;

    /// <inheritdoc />
    public void Set(TenantInfo? tenant) => _current = tenant;
}
