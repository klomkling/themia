namespace Themia.Modules.Messaging;

/// <summary>Configuration for the Themia Messaging module.</summary>
public sealed class MessagingModuleOptions
{
    /// <summary>Name of the connection string (in <c>ConnectionStrings</c>) the module migrates and drains.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>
    /// This service's identity, stamped on every published message as its origin and used by the receiver
    /// to drop messages that arrive back where they started.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>How often the drainer polls when no in-process signal arrives. Default 5s.</summary>
    public int DrainIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum outbox rows claimed per drain cycle. Default 50.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Attempts before a message is marked dead. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed row's lease is held before it is reclaimable. Default 120s.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Whether retention purging runs. Defaults to <see langword="true"/>: this schema is new, so
    /// there is no pre-existing history that enabling it could destroy.</summary>
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>How long delivered rows are kept. Default 7 days.</summary>
    public int SentRetentionDays { get; set; } = 7;

    /// <summary>How long dead-lettered rows are kept. Default 90 days.</summary>
    public int DeadRetentionDays { get; set; } = 90;

    /// <summary>How long inbox admission records are kept. Default 90 days, matching the dead-letter window
    /// so a redelivery can never outlive its admission record.</summary>
    public int InboxRetentionDays { get; set; } = 90;

    /// <summary>Validates the options, throwing if any value is out of range or inconsistent.</summary>
    /// <exception cref="ArgumentException">A required string is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        if (string.IsNullOrWhiteSpace(Origin))
            throw new ArgumentException("Must not be null or whitespace.", nameof(Origin));
        if (DrainIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(DrainIntervalSeconds), DrainIntervalSeconds, "Must be at least 1 second.");
        if (MaxBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), MaxBatchSize, "Must be at least 1.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Must be at least 1.");
        if (LeaseSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(LeaseSeconds), LeaseSeconds, "Must be at least 1 second.");
        if (SentRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(SentRetentionDays), SentRetentionDays, "Must be at least 1 day.");
        if (DeadRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(DeadRetentionDays), DeadRetentionDays, "Must be at least 1 day.");

        // Forgetting an admission record before the sender can stop retrying means a late redelivery is
        // reprocessed as new. Dead-lettering bounds how long the sender keeps trying, so the inbox window
        // must cover it.
        if (InboxRetentionDays < DeadRetentionDays)
            throw new ArgumentOutOfRangeException(
                nameof(InboxRetentionDays),
                InboxRetentionDays,
                $"Must be at least {nameof(DeadRetentionDays)} ({DeadRetentionDays}) so a redelivery cannot outlive its admission record.");
    }
}
