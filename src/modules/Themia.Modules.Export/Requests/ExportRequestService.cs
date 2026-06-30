using Quartz;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Requests;

internal sealed class ExportRequestService(
    IExportRunStore store,
    IExportScheduleStore scheduleStore,
    IExportDefinitionRegistry registry,
    ISchedulerFactory schedulerFactory,
    ITenantContext tenantContext) : IExportRequestService, IExportRunEnqueuer
{
    public async Task<ExportRunView> SubmitAsync(ExportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        EnsureRunnable(submission.DefinitionKey, submission.IncludeSoftDeleted);

        var run = await EnqueueRunAsync(
            new EnqueueRunCommand(
                submission.DefinitionKey,
                submission.ParametersJson,
                submission.Format,
                submission.FileName,
                submission.IncludeSoftDeleted,
                submission.UserId,
                tenantContext.CurrentTenantId),
            cancellationToken).ConfigureAwait(false);

        return ToView(run);
    }

    public async Task<Guid> ScheduleAsync(ExportScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunnable(request.DefinitionKey, request.IncludeSoftDeleted);
        if (!CronExpression.IsValidExpression(request.Cron))
        {
            throw new InvalidOperationException($"'{request.Cron}' is not a valid Quartz cron expression.");
        }

        var schedule = new ExportSchedule
        {
            TenantId = tenantContext.CurrentTenantId,
            UserId = request.UserId,
            DefinitionKey = request.DefinitionKey,
            ParametersJson = request.ParametersJson,
            Format = request.Format,
            Cron = request.Cron,
            Enabled = true,
            IncludeSoftDeleted = request.IncludeSoftDeleted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        schedule.SetId(Guid.NewGuid());
        await scheduleStore.CreateAsync(schedule, cancellationToken).ConfigureAwait(false);

        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var job = JobBuilder.Create<ExportScheduleJob>()
                .WithIdentity($"export-schedule-{schedule.Id:N}", "export")
                .UsingJobData(ExportScheduleJob.ScheduleIdKey, schedule.Id.ToString())
                .Build();
            var trigger = TriggerBuilder.Create().WithCronSchedule(schedule.Cron).Build();
            await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The schedule row exists but its trigger did not register; remove the row so it cannot linger
            // as a schedule that will never fire. CancellationToken.None: compensation must complete.
            await scheduleStore.DeleteAsync(schedule, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return schedule.Id;
    }

    public async Task<IReadOnlyList<ExportRunView>> ListRunsAsync(string? userId, CancellationToken cancellationToken)
    {
        var runs = await store.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return runs.Select(ToView).ToList();
    }

    public async Task<ExportRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return run is null ? null : ToView(run);
    }

    public async Task<ExportRun> EnqueueRunAsync(EnqueueRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var run = new ExportRun
        {
            TenantId = command.TenantId,
            UserId = command.UserId,
            DefinitionKey = command.DefinitionKey,
            ParametersJson = command.ParametersJson,
            Format = command.Format,
            FileName = command.FileName,
            IncludeSoftDeleted = command.IncludeSoftDeleted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        run.SetId(Guid.NewGuid());
        await store.CreateAsync(run, cancellationToken).ConfigureAwait(false);

        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var job = JobBuilder.Create<ExportJob>()
                .WithIdentity($"export-{run.Id:N}", "export")
                .UsingJobData(ExportJob.RunIdKey, run.Id.ToString())
                .Build();
            var trigger = TriggerBuilder.Create().StartNow().Build();
            await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The Pending run was persisted but its job did not schedule; mark it Failed so it is never
            // left orphaned in Pending with no job to execute it. CancellationToken.None: must persist.
            run.MarkFailed($"Failed to schedule the export job: {ex.Message}", DateTimeOffset.UtcNow);
            await store.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return run;
    }

    private void EnsureRunnable(string definitionKey, bool includeSoftDeleted)
    {
        var definition = registry.Find(definitionKey)
            ?? throw new InvalidOperationException($"No export definition registered for key '{definitionKey}'.");
        if (includeSoftDeleted && !definition.AllowsIncludeSoftDeleted)
        {
            throw new InvalidOperationException(
                $"Definition '{definitionKey}' does not allow including soft-deleted rows.");
        }
    }

    private static ExportRunView ToView(ExportRun r) =>
        new(r.Id, r.DefinitionKey, r.Format, r.Status, r.StorageKey, r.SizeBytes, r.ExpiresAt, r.Error, r.CreatedAt, r.CompletedAt);
}
