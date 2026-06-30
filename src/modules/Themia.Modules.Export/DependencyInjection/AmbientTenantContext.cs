using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.DependencyInjection;

/// <summary>An <see cref="ITenantContext"/> that reads the ambient <see cref="TenantContextAccessor"/>.
/// Background Quartz jobs run request-less, so they establish the tenant via
/// <c>BackgroundTenantScope.Begin</c> (which sets <see cref="TenantContextAccessor.CurrentTenantId"/>).
/// This context surfaces that ambient value to <see cref="ExportDbContext"/> and <c>ITenantStorage</c>,
/// so the export/cleanup jobs touch the correct tenant's rows and blobs. Registered with <c>TryAdd</c> so
/// a host that already supplies an accessor-reading context (e.g. the AspNetCore one) wins.</summary>
internal sealed class AmbientTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId? CurrentTenantId => TenantContextAccessor.CurrentTenantId;

    /// <inheritdoc />
    public string? Source => "ambient";
}
