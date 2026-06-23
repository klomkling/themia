namespace Themia.Modules.Notifications;

/// <summary>Configuration for the Themia Notifications module.</summary>
public sealed class NotificationsModuleOptions
{
    /// <summary>Name of the connection string (in <c>ConnectionStrings</c>) the module migrates and drains.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>How often the background drainer polls when no in-process signal arrives. Default 5s.</summary>
    public int DrainIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum outbox rows claimed per drain cycle. Default 50.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Attempts before a message is marked <c>dead</c>. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed (sending) row's lease is held before it is reclaimable. Default 120s.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Validates the options, throwing if any value is out of range.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        if (DrainIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(DrainIntervalSeconds), DrainIntervalSeconds, "Must be at least 1 second.");
        if (MaxBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), MaxBatchSize, "Must be at least 1.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Must be at least 1.");
        if (LeaseSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(LeaseSeconds), LeaseSeconds, "Must be at least 1 second.");
    }
}
