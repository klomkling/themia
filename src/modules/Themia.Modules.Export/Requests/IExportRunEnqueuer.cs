using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Requests;

/// <summary>The shared seam for creating a Pending run and scheduling its one-shot <c>ExportJob</c>. Used by
/// both <see cref="IExportRequestService.SubmitAsync"/> (ambient tenant) and the schedule job (the schedule's
/// tenant), so the run-creation + Quartz-scheduling logic lives in exactly one place.</summary>
internal interface IExportRunEnqueuer
{
    /// <summary>Persists a Pending run for <paramref name="tenantId"/> and schedules a one-shot job firing now.</summary>
    Task<ExportRun> EnqueueRunAsync(
        string definitionKey,
        string? parametersJson,
        ExportFormat format,
        string? fileName,
        bool includeSoftDeleted,
        string? userId,
        TenantId? tenantId,
        CancellationToken cancellationToken);
}
