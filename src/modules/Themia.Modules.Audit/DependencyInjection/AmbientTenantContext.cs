using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Audit.DependencyInjection;

/// <summary>
/// An <see cref="ITenantContext"/> that reads the ambient <see cref="TenantContextAccessor"/>. Registered
/// with <c>TryAdd</c> (mirrors <c>Themia.Modules.Export</c>'s identically-named type) so a host that
/// already supplies a request-scoped <see cref="ITenantContext"/> (e.g. the AspNetCore one) wins; a host
/// with no tenant infrastructure at all still gets a working, host-level-by-default context instead of a
/// missing-service DI failure.
/// </summary>
internal sealed class AmbientTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId? CurrentTenantId => TenantContextAccessor.CurrentTenantId;

    /// <inheritdoc />
    public string? Source => "ambient";
}
