namespace Themia.Messaging.Outbox;

/// <summary>
/// Drain-loop settings for one outbox. Kept separate from any owning module's options so the drainer
/// stays reusable; a module maps its own configuration onto this.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape whose drainer these settings configure, so multiple
/// outboxes can be drained side by side with independent settings in one container.</typeparam>
public sealed class OutboxDrainerOptions<TRow>
    where TRow : IClaimedRow
{
    /// <summary>How often the drainer polls when no in-process signal arrives. Default 5s.</summary>
    public int DrainIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum rows claimed per drain cycle. Default 50.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Attempts before a row is marked dead. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed row's lease is held before it is reclaimable. Default 120s.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the drain loop also purges terminal rows. Defaults to <see langword="false"/> so that
    /// enabling retention is always a deliberate act: switching it on for an existing deployment deletes
    /// history on the first run, which must never arrive as a side effect of a version bump.
    /// </summary>
    public bool PurgeEnabled { get; set; }

    /// <summary>How long successfully-sent rows are kept. Default 7 days.</summary>
    public int SentRetentionDays { get; set; } = 7;

    /// <summary>How long dead-lettered rows are kept. Default 90 days — each one is an unresolved failure.</summary>
    public int DeadRetentionDays { get; set; } = 90;

    /// <summary>Minimum interval between purge passes. Default 24 hours.</summary>
    public int PurgeIntervalHours { get; set; } = 24;

    /// <summary>Rows deleted per statement. Default 1000, keeping each delete's lock hold short.</summary>
    public int PurgeBatchSize { get; set; } = 1000;
}
