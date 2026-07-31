namespace Themia.Messaging.Inbox;

/// <summary>
/// The receiving half of at-least-once delivery: records which messages have been admitted so a
/// redelivery is recognised and dropped rather than applied twice.
/// </summary>
/// <remarks>
/// <para>
/// Deduplication is keyed on (origin, message id) rather than message id alone, so two peers can never
/// collide on an identifier either of them generated independently.
/// </para>
/// <para>
/// This store deduplicates and nothing else. It does NOT decide whether an arriving payload is older
/// than the state already held — that fence belongs in the application's own write
/// (<c>... WHERE version &lt; @v</c> against its entity), where the version lives as long as the entity
/// does. <see cref="Messages.MessageEnvelope.EntityKey"/> and <see cref="Messages.MessageEnvelope.Version"/>
/// are carried to the receiver for exactly that purpose.
/// </para>
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Atomically records the message as admitted and reports whether the caller should process it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWO OBLIGATIONS ON THE CALLER, both load-bearing.
    /// </para>
    /// <para>
    /// (1) ADMIT BEFORE APPLYING. An application that applies the payload first and admits afterwards
    /// gets no protection at all.
    /// </para>
    /// <para>
    /// (2) ADMISSION MUST COMMIT WITH THE STATE CHANGE. This call participates in the caller's
    /// transaction; the caller commits both together. If admission committed separately, a crash between
    /// admitting and applying would lose the message permanently — the redelivery answers
    /// <see cref="InboxAdmission.Duplicate"/> while the state was never applied, which is indistinguishable
    /// from correct deduplication.
    /// </para>
    /// <para>
    /// The insert-or-ignore must happen in ONE statement: a read-then-write would let two concurrent
    /// deliveries of the same message both observe "not seen" and both process it.
    /// </para>
    /// </remarks>
    /// <param name="origin">The system that originated the message.</param>
    /// <param name="messageId">The sender's stable message identifier.</param>
    /// <param name="type">The logical message type, recorded for diagnostics.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>Whether the message is new or a duplicate.</returns>
    Task<InboxAdmission> TryAdmitAsync(
        string origin,
        Guid messageId,
        string type,
        CancellationToken ct);
}
