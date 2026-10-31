using System.Data.Common;

namespace Themia.Conversations;

/// <summary>What happened to a conversation or a participant outside the message flow.</summary>
public enum ConversationLifecycleKind
{
    /// <summary>A conversation and everything in it was deleted. <see cref="ConversationLifecycleEvent.Count"/> is the message count.</summary>
    Purged = 0,

    /// <summary>A person's data in one conversation was erased. <see cref="ConversationLifecycleEvent.ParticipantId"/> is the participant row.</summary>
    ParticipantErased = 1,

    /// <summary>A legal hold was placed.</summary>
    HoldPlaced = 2,

    /// <summary>A legal hold was lifted.</summary>
    HoldReleased = 3,
}

/// <summary>A lifecycle event: ids and a count, never text, names or contacts.</summary>
/// <param name="Id">The event id.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConversationId">The conversation (for a purge, the one that no longer exists).</param>
/// <param name="Kind">What happened.</param>
/// <param name="ParticipantId">The participant, for <see cref="ConversationLifecycleKind.ParticipantErased"/>; otherwise <see langword="null"/>.</param>
/// <param name="Count">Messages purged, or messages redacted for an erasure; otherwise 0.</param>
public sealed record ConversationLifecycleEvent(Guid Id, string TenantId, Guid ConversationId, ConversationLifecycleKind Kind, Guid? ParticipantId, long Count);

/// <summary>Receives lifecycle events inside the transaction that made the change, so the change and its record commit together or not at all.</summary>
/// <remarks>The default does nothing; <c>conversation_events</c> is not used because a purge deletes it. An app that needs an audit trail registers its own.</remarks>
public interface IConversationLifecycleSink
{
    /// <summary>Records the event on the connection and transaction of the change.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The open transaction; throwing rolls the change back.</param>
    /// <param name="lifecycleEvent">The event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the event is recorded.</returns>
    Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default);
}
