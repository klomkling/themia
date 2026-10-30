using System.Data.Common;

namespace Themia.Conversations;

/// <summary>What happened in a conversation, handed to <see cref="IConversationEventSink"/> inside the transaction that caused it. Carries ids and counts, never the text.</summary>
/// <param name="Id">The event id: a subscriber dedupes on it, because delivery is at least once.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Kind">What happened.</param>
/// <param name="MessageId">The message.</param>
/// <param name="Seq">Its sequence number.</param>
/// <param name="RecipientParticipantIds">The other active participants, who should hear of it.</param>
public sealed record ConversationEvent(Guid Id, string TenantId, Guid ConversationId, ConversationEventKind Kind, Guid MessageId, long Seq, IReadOnlyList<Guid> RecipientParticipantIds);

/// <summary>
/// A transaction-aware port: called inside the post transaction, so whatever the sink writes commits or rolls back with the message. The default
/// registration writes a row into <c>conversation_events</c>; an app may replace it (for example to enlist in its own outbox on the same connection).
/// </summary>
public interface IConversationEventSink
{
    /// <summary>Records <paramref name="conversationEvent"/> on <paramref name="connection"/> within <paramref name="transaction"/>. An exception rolls the post back.</summary>
    /// <param name="connection">The open connection of the post transaction.</param>
    /// <param name="transaction">The post transaction.</param>
    /// <param name="conversationEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationEvent conversationEvent, CancellationToken cancellationToken = default);
}
