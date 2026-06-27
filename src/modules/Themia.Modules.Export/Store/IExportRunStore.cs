using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

/// <summary>Persistence for export runs. Tenant-scoped except the two deliberate cross-tenant reads
/// the background jobs need (keyed by unguessable GUIDs / used only to discover the owning tenant).</summary>
public interface IExportRunStore
{
    /// <summary>Inserts a new run.</summary>
    Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken);

    /// <summary>Loads a run by id across tenants (the job then establishes that tenant's scope).</summary>
    Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Persists status/result changes.</summary>
    Task UpdateAsync(ExportRun run, CancellationToken cancellationToken);

    /// <summary>Lists the current tenant's runs (optionally filtered by user), newest first.</summary>
    Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken cancellationToken);

    /// <summary>Finds succeeded runs whose bytes have expired, across all tenants (for cleanup).</summary>
    Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
