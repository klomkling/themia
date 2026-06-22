namespace Themia.Modules.Notifications.Entities;

/// <summary>Lifecycle state of an outbox message.</summary>
public enum OutboxStatus
{
    /// <summary>Awaiting its first (or a retried) send.</summary>
    Pending = 0,
    /// <summary>Claimed by a drainer and in flight.</summary>
    Sending = 1,
    /// <summary>Delivered to the provider successfully.</summary>
    Sent = 2,
    /// <summary>Last attempt failed; eligible for retry until the attempt cap.</summary>
    Failed = 3,
    /// <summary>Exhausted the attempt cap; will not be retried.</summary>
    Dead = 4,
}
