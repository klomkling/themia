namespace Themia.Modules.Scheduling;

/// <summary>
/// EF Core entity representing a single job-execution history record persisted by
/// <see cref="EfExecutionHistoryStore"/>.
/// </summary>
/// <remarks>
/// This entity is intentionally NOT tenant-scoped (no TenantId, does not implement
/// <c>ITenantEntity</c>). Execution history is process-wide scheduler infrastructure
/// that an administrator inspects across all tenants; it is not a per-tenant data
/// domain. The Quartz scheduler is a single process-wide service, and its history
/// records identify the scheduler instance, not a tenant.
/// </remarks>
public class ExecutionHistoryRecord
{
    /// <summary>Gets or sets the unique fire-instance identifier assigned by Quartz (primary key).</summary>
    public string FireInstanceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the scheduler instance that fired the job.</summary>
    public string? SchedulerInstanceId { get; set; }

    /// <summary>Gets or sets the name of the scheduler that fired the job.</summary>
    public string? SchedulerName { get; set; }

    /// <summary>Gets or sets the job key in <c>group.name</c> form.</summary>
    public string? Job { get; set; }

    /// <summary>Gets or sets the trigger key in <c>group.name</c> form.</summary>
    public string? Trigger { get; set; }

    /// <summary>Gets or sets the time the job was scheduled to fire (UTC), or <see langword="null"/> for ad-hoc triggers.</summary>
    public DateTimeOffset? ScheduledFireTimeUtc { get; set; }

    /// <summary>Gets or sets the time the job actually fired (UTC).</summary>
    public DateTimeOffset ActualFireTimeUtc { get; set; }

    /// <summary>Gets or sets whether this job is being recovered after a previous scheduler failure.</summary>
    public bool Recovering { get; set; }

    /// <summary>Gets or sets whether the job execution was vetoed by a trigger listener.</summary>
    public bool Vetoed { get; set; }

    /// <summary>Gets or sets the time the job finished (UTC), or <see langword="null"/> if not yet completed.</summary>
    public DateTimeOffset? FinishedTimeUtc { get; set; }

    /// <summary>Gets or sets the base exception message if the job threw, otherwise <see langword="null"/>.</summary>
    public string? ExceptionMessage { get; set; }
}
