using Themia.Messaging.Messages;

namespace Themia.Messaging.Outbox;

/// <summary>
/// Stages messages into the caller's current unit of work, so a published message commits atomically
/// with the work that produced it (no "sent but rolled back", and no "committed but never sent").
/// </summary>
public interface IMessageOutboxStore
{
    /// <summary>Stages an insert of <paramref name="message"/>; the caller's UoW commit persists it.</summary>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="ct">A token to observe while waiting for the staging operation to complete.</param>
    /// <returns>A task that completes once the insert has been staged.</returns>
    Task EnqueueAsync(MessageEnvelope message, CancellationToken ct = default);
}
