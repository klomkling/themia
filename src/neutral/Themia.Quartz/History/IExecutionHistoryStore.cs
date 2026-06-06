namespace Themia.Quartz;

/// <summary>Statistics counters for a scheduler's job execution history.</summary>
[Serializable]
public class JobStats
{
    /// <summary>Total number of jobs that completed successfully.</summary>
    public int TotalJobsExecuted { get; set; }

    /// <summary>Total number of jobs that failed with an exception.</summary>
    public int TotalJobsFailed { get; set; }
}

/// <summary>A single job-execution history record captured by <see cref="ExecutionHistoryPlugin"/>.</summary>
[Serializable]
public class ExecutionHistoryEntry
{
    /// <summary>The unique fire instance identifier assigned by Quartz.</summary>
    public string? FireInstanceId { get; set; }

    /// <summary>The scheduler instance that fired the job.</summary>
    public string? SchedulerInstanceId { get; set; }

    /// <summary>The name of the scheduler that fired the job.</summary>
    public string? SchedulerName { get; set; }

    /// <summary>The job key in <c>group.name</c> form.</summary>
    public string? Job { get; set; }

    /// <summary>The trigger key in <c>group.name</c> form.</summary>
    public string? Trigger { get; set; }

    /// <summary>The time the job was scheduled to fire (UTC), or <see langword="null"/> for ad-hoc triggers.</summary>
    public DateTimeOffset? ScheduledFireTimeUtc { get; set; }

    /// <summary>The time the job actually fired (UTC).</summary>
    public DateTimeOffset ActualFireTimeUtc { get; set; }

    /// <summary>Whether this job is being recovered after a previous scheduler failure.</summary>
    public bool Recovering { get; set; }

    /// <summary>Whether the job execution was vetoed by a trigger listener.</summary>
    public bool Vetoed { get; set; }

    /// <summary>The time the job finished (UTC), or <see langword="null"/> if not yet completed.</summary>
    public DateTimeOffset? FinishedTimeUtc { get; set; }

    /// <summary>The base exception message if the job threw, otherwise <see langword="null"/>.</summary>
    public string? ExceptionMessage { get; set; }
}

/// <summary>
/// Contract for a store that records recent job-execution history for a named Quartz scheduler.
/// Implementations include the built-in <see cref="InProcExecutionHistoryStore"/> (in-memory)
/// and the EF-backed store provided by <c>Themia.Modules.Scheduling</c>.
/// </summary>
public interface IExecutionHistoryStore
{
    /// <summary>The scheduler name this store is scoped to. Set by <see cref="ExecutionHistoryPlugin"/> on start-up.</summary>
    string SchedulerName { get; set; }

    /// <summary>Returns the entry for the given fire-instance ID, or <see langword="null"/> if not found.</summary>
    Task<ExecutionHistoryEntry?> Get(string fireInstanceId);

    /// <summary>Creates or updates the execution history entry.</summary>
    Task Save(ExecutionHistoryEntry entry);

    /// <summary>Removes old entries, retaining the most recent entries per trigger.</summary>
    Task Purge();

    /// <summary>Returns the last <paramref name="limitPerJob"/> entries for each distinct job.</summary>
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryJob(int limitPerJob);

    /// <summary>Returns the last <paramref name="limitPerTrigger"/> entries for each distinct trigger.</summary>
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryTrigger(int limitPerTrigger);

    /// <summary>Returns the most recent <paramref name="limit"/> entries across all jobs.</summary>
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLast(int limit);

    /// <summary>Returns the running total of jobs that completed successfully.</summary>
    Task<int> GetTotalJobsExecuted();

    /// <summary>Returns the running total of jobs that failed with an exception.</summary>
    Task<int> GetTotalJobsFailed();

    /// <summary>Increments the successful-execution counter by one.</summary>
    Task IncrementTotalJobsExecuted();

    /// <summary>Increments the failed-execution counter by one.</summary>
    Task IncrementTotalJobsFailed();
}
