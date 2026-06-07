namespace Themia.Modules.Scheduling;

/// <summary>
/// EF Core entity holding running job-execution counters for a named Quartz scheduler.
/// </summary>
/// <remarks>
/// One row per scheduler name. Counters are incremented in-place via
/// <see cref="EfExecutionHistoryStore.IncrementTotalJobsExecuted"/> and
/// <see cref="EfExecutionHistoryStore.IncrementTotalJobsFailed"/> using a
/// raw SQL <c>UPDATE … SET counter = counter + 1</c> to avoid optimistic-concurrency
/// conflicts under concurrent job execution.
/// Like <see cref="ExecutionHistoryRecord"/>, this entity is NOT tenant-scoped —
/// scheduler counters are process-wide infrastructure.
/// </remarks>
public class SchedulerStatsRecord
{
    /// <summary>Gets or sets the scheduler name (primary key).</summary>
    public string SchedulerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the running total of jobs that completed successfully.</summary>
    public int TotalJobsExecuted { get; set; }

    /// <summary>Gets or sets the running total of jobs that failed with an exception.</summary>
    public int TotalJobsFailed { get; set; }
}
