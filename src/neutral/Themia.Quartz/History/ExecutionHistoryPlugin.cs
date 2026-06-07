using Quartz;
using Quartz.Impl.Matchers;
using Quartz.Spi;

namespace Themia.Quartz;

/// <summary>
/// A Quartz scheduler plugin that records job execution history into an <see cref="IExecutionHistoryStore"/>.
/// Register this plugin when building your scheduler to enable the execution-history dashboard.
/// </summary>
/// <remarks>
/// Vendored from SilkierQuartz (https://github.com/maikebing/SilkierQuartz,
/// commit 4b974e080d369c588194e84642a9be875175f3fd) under the MIT licence.
/// Re-namespaced from <c>Quartz.Plugins.RecentHistory</c> to <c>Themia.Quartz</c>.
/// </remarks>
public class ExecutionHistoryPlugin : ISchedulerPlugin, IJobListener
{
    private IScheduler? _scheduler;
    private IExecutionHistoryStore? _store;

    /// <summary>The plugin name, set by Quartz during <see cref="Initialize"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional type of a custom <see cref="IExecutionHistoryStore"/> implementation to instantiate
    /// when no store has been pre-registered on the scheduler context.
    /// When <see langword="null"/>, <see cref="InProcExecutionHistoryStore"/> is used.
    /// </summary>
    public Type? StoreType { get; set; }

    /// <inheritdoc/>
    public Task Initialize(string pluginName, IScheduler scheduler, CancellationToken cancellationToken = default)
    {
        Name = pluginName;
        _scheduler = scheduler;
        _scheduler.ListenerManager.AddJobListener(this, EverythingMatcher<JobKey>.AllJobs());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Start(CancellationToken cancellationToken = default)
    {
        _store = _scheduler!.Context.GetExecutionHistoryStore();

        if (_store == null)
        {
            if (StoreType != null)
                _store = (IExecutionHistoryStore)Activator.CreateInstance(StoreType)!;

            _store ??= new InProcExecutionHistoryStore();

            _scheduler.Context.SetExecutionHistoryStore(_store);
        }

        _store.SchedulerName = _scheduler.SchedulerName;

        return _store.Purge();
    }

    /// <inheritdoc/>
    public Task Shutdown(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var entry = new ExecutionHistoryEntry
        {
            FireInstanceId = context.FireInstanceId,
            SchedulerInstanceId = context.Scheduler.SchedulerInstanceId,
            SchedulerName = context.Scheduler.SchedulerName,
            ActualFireTimeUtc = context.FireTimeUtc.UtcDateTime,
            ScheduledFireTimeUtc = context.ScheduledFireTimeUtc?.UtcDateTime,
            Recovering = context.Recovering,
            Job = context.JobDetail.Key.ToString(),
            Trigger = context.Trigger.Key.ToString(),
        };
        return _store!.Save(entry);
    }

    /// <inheritdoc/>
    public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        var entry = await _store!.Get(context.FireInstanceId);
        if (entry != null)
        {
            entry.FinishedTimeUtc = DateTime.UtcNow;
            entry.ExceptionMessage = jobException?.GetBaseException()?.Message;
            await _store.Save(entry);
        }

        if (jobException == null)
            await _store.IncrementTotalJobsExecuted();
        else
            await _store.IncrementTotalJobsFailed();
    }

    /// <inheritdoc/>
    public async Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var entry = await _store!.Get(context.FireInstanceId);
        if (entry != null)
        {
            entry.Vetoed = true;
            await _store.Save(entry);
        }
    }
}
