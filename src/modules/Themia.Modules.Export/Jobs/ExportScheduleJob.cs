using Microsoft.Extensions.Logging;
using Quartz;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;

namespace Themia.Modules.Export.Jobs;

/// <summary>Fires on a schedule's cron trigger: loads the schedule cross-tenant, establishes its tenant
/// scope, then enqueues a run via the same one-shot <c>ExportJob</c> path as an on-demand submit. Quartz
/// job data carries <c>scheduleId</c>.</summary>
[DisallowConcurrentExecution]
internal sealed class ExportScheduleJob(
    IExportScheduleStore scheduleStore,
    IExportRunEnqueuer enqueuer,
    ILogger<ExportScheduleJob> logger) : IJob
{
    public const string ScheduleIdKey = "scheduleId";

    public async Task Execute(IJobExecutionContext context)
    {
        var scheduleId = Guid.Parse(context.MergedJobDataMap.GetString(ScheduleIdKey)!);
        var schedule = await scheduleStore.GetByIdIgnoringTenantAsync(scheduleId, context.CancellationToken).ConfigureAwait(false);
        if (schedule is null || !schedule.Enabled)
        {
            logger.LogWarning("Export schedule {ScheduleId} not found or disabled; skipping.", scheduleId);
            return;
        }

        using var _ = BackgroundTenantScope.Begin(schedule.TenantId);

        // Relative-param markers (if any) are resolved by the definition inside RowsAsync using
        // DateTimeOffset.UtcNow; the schedule job performs no date math for v1.
        await enqueuer.EnqueueRunAsync(
            schedule.DefinitionKey,
            schedule.ParametersJson,
            schedule.Format,
            fileName: null,
            schedule.IncludeSoftDeleted,
            schedule.UserId,
            schedule.TenantId,
            context.CancellationToken).ConfigureAwait(false);
    }
}
