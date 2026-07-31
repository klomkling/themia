namespace Themia.Messaging.Inbox;

/// <summary>
/// The receiving half of at-least-once delivery: records which messages have been admitted so a
/// redelivery is recognised and dropped rather than applied twice.
/// </summary>
/// <remarks>
/// Deduplication is keyed on (origin, message id) rather than message id alone, so two peers can never
/// collide on an identifier either of them generated independently.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Atomically records the message as admitted and reports whether the caller should process it.
    /// The insert-or-ignore must happen in ONE statement: a read-then-write would let two concurrent
    /// deliveries of the same message both observe "not seen" and both process it, which is exactly the
    /// duplicate this exists to prevent.
    /// </summary>
    /// <param name="origin">The system that originated the message.</param>
    /// <param name="messageId">The sender's stable message identifier.</param>
    /// <param name="entityKey">The key a staleness fence applies within, or <see langword="null"/>.</param>
    /// <param name="version">The monotonic version for <paramref name="entityKey"/>, or <see langword="null"/>.</param>
    /// <param name="receivedAt">The admission timestamp recorded on the row.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>Whether the message is new, a duplicate, or superseded by a newer version.</returns>
    Task<InboxAdmission> TryAdmitAsync(
        string origin,
        Guid messageId,
        string? entityKey,
        long? version,
        DateTimeOffset receivedAt,
        CancellationToken ct);
}
