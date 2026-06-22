using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Stages outbox messages into the caller's current unit of work, so a queued notification
/// commits atomically with the work that triggered it (no "sent but rolled back").
/// </summary>
public interface IOutboxStore
{
    /// <summary>Stages an insert of <paramref name="message"/>; the caller's UoW commit persists it.</summary>
    /// <param name="message">The pending outbox message to enqueue.</param>
    /// <param name="ct">A token to observe while waiting for the staging operation to complete.</param>
    /// <returns>A task that completes once the insert has been staged.</returns>
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default);
}
