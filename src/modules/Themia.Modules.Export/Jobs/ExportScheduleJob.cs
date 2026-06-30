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
        // GetString returns null for a missing key, so a single TryParse covers both missing and malformed.
        var scheduleIdValue = context.MergedJobDataMap.GetString(ScheduleIdKey);
        if (!Guid.TryParse(scheduleIdValue, out var scheduleId))
        {
            // Throw (not swallow) so Quartz surfaces the fault to monitoring rather than recording success.
            throw new InvalidOperationException(
                $"Export schedule job invoked with missing or malformed '{ScheduleIdKey}' job data ('{scheduleIdValue}').");
        }

        var schedule = await scheduleStore.GetByIdIgnoringTenantAsync(scheduleId, context.CancellationToken).ConfigureAwait(false);
        if (schedule is null)
        {
            logger.LogWarning("Export schedule {ScheduleId} not found; skipping.", scheduleId);
            return;
        }

        if (!schedule.Enabled)
        {
            // A disabled schedule whose trigger is still firing means its trigger was never unscheduled —
            // a misconfiguration worth surfacing at Information (visible in production), not an error.
            logger.LogInformation("Export schedule {ScheduleId} is disabled but still firing; skipping.", scheduleId);
            return;
        }

        using var _ = BackgroundTenantScope.Begin(schedule.TenantId);

        // Relative-param markers (if any) are resolved by the definition inside RowsAsync using
        // DateTimeOffset.UtcNow; the schedule job performs no date math for v1.
        try
        {
            await enqueuer.EnqueueRunAsync(
                new EnqueueRunCommand(
                    schedule.DefinitionKey,
                    schedule.ParametersJson,
                    schedule.Format,
                    FileName: null,
                    schedule.IncludeSoftDeleted,
                    schedule.UserId,
                    schedule.TenantId),
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enrich with context (the top-level Quartz handler logs it once — see THEMIA101 / the
            // no-double-logging rule), so the failure is attributable to this schedule and definition.
            throw new InvalidOperationException(
                $"Export schedule {scheduleId} failed to enqueue a run for definition '{schedule.DefinitionKey}'.", ex);
        }
    }
}
