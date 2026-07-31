namespace Themia.Modules.Messaging.Entities;

/// <summary>Lifecycle state of an outbox row. Values are persisted as integers and must not be renumbered.</summary>
public enum OutboxStatus
{
    /// <summary>Awaiting its first delivery attempt.</summary>
    Pending = 0,

    /// <summary>Claimed by a drainer under a lease.</summary>
    Sending = 1,

    /// <summary>Delivered.</summary>
    Sent = 2,

    /// <summary>Failed and eligible for another attempt after backoff.</summary>
    Failed = 3,

    /// <summary>Permanently undeliverable; no further attempts.</summary>
    Dead = 4,
}
