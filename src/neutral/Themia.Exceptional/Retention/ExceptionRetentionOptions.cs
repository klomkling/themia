namespace Themia.Exceptional.Retention;

/// <summary>Age-based cleanup of stored exceptions. Off by default; protected rows are never deleted.</summary>
public sealed class ExceptionRetentionOptions
{
    /// <summary>Largest allowed <see cref="BatchSize"/>: stays below SQL Server's lock-escalation threshold (~5000 locks).</summary>
    public const int MaxBatchSize = 4000;

    /// <summary>Largest allowed <see cref="Days"/> (100 years); keeps the cutoff arithmetic in range.</summary>
    public const int MaxDays = 36500;

    /// <summary>Largest allowed <see cref="SweepInterval"/> (30 days); keeps <see cref="Task.Delay(TimeSpan)"/> in range.</summary>
    public static readonly TimeSpan MaxSweepInterval = TimeSpan.FromDays(30);

    /// <summary>Enables the hosted sweep. Default <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Rows older than this many days (by <c>CreationDate</c>) are deleted. At least 1, and longer than <see cref="ExceptionalOptions.RollupPeriod"/>. Default 30.</summary>
    public int Days { get; set; } = 30;

    /// <summary>Time between sweeps. At least one minute. Default one day.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Rows deleted per statement, 1 to <see cref="MaxBatchSize"/>. Default 1000.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Statements per sweep; a larger backlog is finished by the next sweep. At least 1. Default 100.</summary>
    public int MaxBatchesPerSweep { get; set; } = 100;

    internal void Validate(TimeSpan rollupPeriod)
    {
        if (!Enabled)
            return;
        if (Days is < 1 or > MaxDays)
            throw new InvalidOperationException($"ExceptionalOptions.Retention.Days must be between 1 and {MaxDays}.");
        if (TimeSpan.FromDays(Days) <= rollupPeriod)
            throw new InvalidOperationException("ExceptionalOptions.Retention.Days must be longer than ExceptionalOptions.RollupPeriod.");
        if (SweepInterval < TimeSpan.FromMinutes(1) || SweepInterval > MaxSweepInterval)
            throw new InvalidOperationException("ExceptionalOptions.Retention.SweepInterval must be between one minute and 30 days.");
        if (BatchSize is < 1 or > MaxBatchSize)
            throw new InvalidOperationException($"ExceptionalOptions.Retention.BatchSize must be between 1 and {MaxBatchSize}.");
        if (MaxBatchesPerSweep < 1)
            throw new InvalidOperationException("ExceptionalOptions.Retention.MaxBatchesPerSweep must be at least 1.");
    }
}
