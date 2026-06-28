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
        var runId = Guid.Parse(context.MergedJobDataMap.GetString(RunIdKey)!);
        var run = await store.GetByIdIgnoringTenantAsync(runId, context.CancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            logger.LogWarning("Export run {RunId} not found; skipping.", runId);
            return;
        }

        using var _ = BackgroundTenantScope.Begin(run.TenantId);
        try
        {
            run.Status = ExportRunStatus.Running;
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

            var key = $"exports/{run.TenantId?.Value ?? "global"}/{run.Id:N}{Extension(run.Format)}";
            using (var ms = new MemoryStream(result.Content))
            {
                await storage.PutAsync(key, ms, new StoragePutOptions(result.ContentType), context.CancellationToken).ConfigureAwait(false);
            }

            run.StorageKey = key;
            run.FileName = result.FileName;
            run.SizeBytes = result.Content.LongLength;
            run.ExpiresAt = DateTimeOffset.UtcNow + options.Value.Retention;
            run.Status = ExportRunStatus.Succeeded;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);

            await NotifyAsync(run, key, succeeded: true, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export run {RunId} failed.", run.Id);
            run.Status = ExportRunStatus.Failed;
            run.Error = ex.Message;
            run.CompletedAt = DateTimeOffset.UtcNow;
            // Use CancellationToken.None: the host may have cancelled context.CancellationToken
            // during shutdown, which would prevent the Failed status from persisting and the
            // failure notification from being sent, orphaning the run in Running state.
            await store.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
            await NotifyAsync(run, storageKey: null, succeeded: false, CancellationToken.None).ConfigureAwait(false);
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
