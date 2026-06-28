using Microsoft.Extensions.Logging;
using Quartz;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Store;
using Themia.Modules.Storage;

namespace Themia.Modules.Export.Jobs;

/// <summary>Recurring retention sweep: deletes expired export blobs (per tenant) and marks runs Expired.</summary>
[DisallowConcurrentExecution]
internal sealed class CleanupJob(
    IExportRunStore store,
    ITenantStorage storage,
    ILogger<CleanupJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await store.FindExpiredAcrossTenantsAsync(now, context.CancellationToken).ConfigureAwait(false);

        foreach (var group in expired.GroupBy(r => r.TenantId))
        {
            using var _ = BackgroundTenantScope.Begin(group.Key);
            foreach (var run in group)
            {
                try
                {
                    if (run.StorageKey is not null)
                    {
                        await storage.DeleteAsync(run.StorageKey, context.CancellationToken).ConfigureAwait(false);
                    }

                    run.Status = ExportRunStatus.Expired;
                    await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to expire run {RunId}.", run.Id);
                }
            }
        }
    }
}
