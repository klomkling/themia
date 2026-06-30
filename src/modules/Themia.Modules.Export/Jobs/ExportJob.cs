using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Themia.Export;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Store;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Storage;
using Themia.Notifications;
using Themia.Storage;

namespace Themia.Modules.Export.Jobs;

/// <summary>Runs one export: resolve definition, produce bytes, persist to Storage, notify. Quartz job
/// data carries <c>runId</c>. Any exception marks the run Failed and notifies (no retry).</summary>
[DisallowConcurrentExecution]
internal sealed class ExportJob(
    IExportRunStore store,
    IExportDefinitionRegistry registry,
    ITenantStorage storage,
    INotificationDispatcher notifier,
    IDataFilterScope filterScope,
    IOptions<ExportModuleOptions> options,
    ILogger<ExportJob> logger) : IJob
{
    public const string RunIdKey = "runId";

    public async Task Execute(IJobExecutionContext context)
    {
        // GetString returns null for a missing key, so a single TryParse covers both missing and malformed.
        var runIdValue = context.MergedJobDataMap.GetString(RunIdKey);
        if (!Guid.TryParse(runIdValue, out var runId))
        {
            // Throw (not swallow) so Quartz surfaces the fault to monitoring rather than recording success.
            throw new InvalidOperationException(
                $"Export job invoked with missing or malformed '{RunIdKey}' job data ('{runIdValue}').");
        }

        var run = await store.GetByIdIgnoringTenantAsync(runId, context.CancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            logger.LogWarning("Export run {RunId} not found; skipping.", runId);
            return;
        }

        using var _ = BackgroundTenantScope.Begin(run.TenantId);
        string key;
        try
        {
            run.MarkRunning(DateTimeOffset.UtcNow);
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);

            var definition = registry.Find(run.DefinitionKey)
                ?? throw new InvalidOperationException($"No export definition registered for key '{run.DefinitionKey}'.");

            var exportContext = new ExportContext
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                ParametersJson = run.ParametersJson,
                Format = run.Format,
                FileName = run.FileName,
                IncludeSoftDeleted = run.IncludeSoftDeleted,
            };

            var result = run.IncludeSoftDeleted && definition.AllowsIncludeSoftDeleted
                ? await RunWithSoftDeleteAsync(definition, exportContext, context.CancellationToken).ConfigureAwait(false)
                : await definition.ExportAsync(exportContext, context.CancellationToken).ConfigureAwait(false);

            key = $"exports/{run.TenantId?.Value ?? "global"}/{run.Id:N}{Extension(run.Format)}";
            using (var ms = new MemoryStream(result.Content))
            {
                await storage.PutAsync(key, ms, new StoragePutOptions(result.ContentType), context.CancellationToken).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            run.MarkSucceeded(key, result.FileName, result.Content.LongLength, now + options.Value.Retention, now);
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export run {RunId} failed.", run.Id);
            await RecordFailureAsync(run, ex).ConfigureAwait(false);
            return;
        }

        // Best-effort completion notification. The export already succeeded and is stored, so a notification
        // failure must neither fail the job (which would re-run the export) nor undo the persisted Succeeded
        // status — log it once and move on.
        try
        {
            await NotifyAsync(run, key, succeeded: true, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Export run {RunId} succeeded but sending its completion notification failed.", run.Id);
        }
    }

    /// <summary>Persists the Failed status and dispatches the failure notification on the error path. Uses
    /// <see cref="CancellationToken.None"/> so a host shutdown that cancelled the job token cannot leave the
    /// run orphaned in Running. Any secondary failure here is logged (not rethrown), so it cannot mask the
    /// original cause — but it is surfaced rather than swallowed silently.</summary>
    private async Task RecordFailureAsync(ExportRun run, Exception cause)
    {
        run.MarkFailed(cause.Message, DateTimeOffset.UtcNow);
        try
        {
            await store.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
            await NotifyAsync(run, storageKey: null, succeeded: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception recordEx)
        {
            logger.LogError(recordEx, "Failed to record the failure of export run {RunId}; it may remain in Running.", run.Id);
        }
    }

    private async Task<ExportResult> RunWithSoftDeleteAsync(
        IExportDefinition definition, ExportContext ctx, CancellationToken ct)
    {
        using (filterScope.BypassSoftDeleteFilter())
        {
            return await definition.ExportAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    private async Task NotifyAsync(ExportRun run, string? storageKey, bool succeeded, CancellationToken ct)
    {
        if (run.UserId is null)
        {
            return;
        }

        string body;
        if (succeeded && storageKey is not null)
        {
            var url = await storage.GetDownloadUrlAsync(storageKey, options.Value.LinkTtl, ct).ConfigureAwait(false);
            body = $"Your export '{run.DefinitionKey}' is ready: {url}";
        }
        else
        {
            body = $"Your export '{run.DefinitionKey}' could not be completed. Please try again or contact support.";
        }

        await notifier.DispatchAsync(
            new NotificationRequest
            {
                UserId = run.UserId,
                Channels = [NotificationChannel.Email, NotificationChannel.InApp],
                Subject = succeeded ? "Export ready" : "Export failed",
                Body = body,
            },
            ct).ConfigureAwait(false);
    }

    private static string Extension(ExportFormat format) =>
        format == ExportFormat.Xlsx ? ".xlsx" : ".csv";
}
