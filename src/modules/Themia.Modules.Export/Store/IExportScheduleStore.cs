using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

/// <summary>Persistence for export schedules. Tenant-scoped except the cross-tenant by-id read the
/// background schedule job needs (keyed by an unguessable GUID, used only to discover the owning tenant).</summary>
internal interface IExportScheduleStore
{
    /// <summary>Inserts a new schedule.</summary>
    Task<ExportSchedule> CreateAsync(ExportSchedule schedule, CancellationToken cancellationToken);

    /// <summary>Loads a schedule by id across tenants (the job then establishes that tenant's scope).</summary>
    Task<ExportSchedule?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken);
}
