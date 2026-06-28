using Quartz;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;

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
            submission.DefinitionKey,
            submission.ParametersJson,
            submission.Format,
            submission.FileName,
            submission.IncludeSoftDeleted,
            submission.UserId,
            tenantContext.CurrentTenantId,
            cancellationToken).ConfigureAwait(false);

        return ToView(run);
    }

    public async Task<Guid> ScheduleAsync(ExportScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunnable(request.DefinitionKey, request.IncludeSoftDeleted);

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

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<ExportScheduleJob>()
            .WithIdentity($"export-schedule-{schedule.Id:N}", "export")
            .UsingJobData(ExportScheduleJob.ScheduleIdKey, schedule.Id.ToString())
            .Build();
        var trigger = TriggerBuilder.Create().WithCronSchedule(schedule.Cron).Build();
        await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);

        return schedule.Id;
    }

    public async Task<IReadOnlyList<ExportRunView>> ListRunsAsync(string? userId, CancellationToken cancellationToken)
    {
        var runs = await store.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return runs.Select(ToView).ToList();
    }

    public async Task<ExportRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var runs = await store.ListAsync(userId: null, cancellationToken).ConfigureAwait(false);
        var run = runs.FirstOrDefault(r => r.Id == id);
        return run is null ? null : ToView(run);
    }

    public async Task<ExportRun> EnqueueRunAsync(
        string definitionKey,
        string? parametersJson,
        ExportFormat format,
        string? fileName,
        bool includeSoftDeleted,
        string? userId,
        TenantId? tenantId,
        CancellationToken cancellationToken)
    {
        var run = new ExportRun
        {
            TenantId = tenantId,
            UserId = userId,
            DefinitionKey = definitionKey,
            ParametersJson = parametersJson,
            Format = format,
            FileName = fileName,
            IncludeSoftDeleted = includeSoftDeleted,
            Status = ExportRunStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        run.SetId(Guid.NewGuid());
        await store.CreateAsync(run, cancellationToken).ConfigureAwait(false);

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<ExportJob>()
            .WithIdentity($"export-{run.Id:N}", "export")
            .UsingJobData(ExportJob.RunIdKey, run.Id.ToString())
            .Build();
        var trigger = TriggerBuilder.Create().StartNow().Build();
        await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);

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
