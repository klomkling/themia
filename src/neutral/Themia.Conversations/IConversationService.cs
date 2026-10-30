namespace Themia.Conversations;

/// <summary>
/// The conversation store. The tenant is an explicit argument of every call, never ambient. Every method asks <see cref="IConversationAccessPolicy"/> first,
/// then resolves the acting participant inside its own transaction (this tenant and conversation, <c>LeftAt</c> empty, kind matching the actor); the
/// <see cref="ConversationActor.ParticipantId"/> of a guest is never trusted as given. A failure to authorise comes back as <c>Refused</c>, the same answer
/// for a conversation that does not exist, so a caller cannot probe another tenant's ids.
/// </summary>
/// <exception cref="ArgumentException">A tenant, subject, user reference or client message id is empty, longer than its column, or has leading or trailing
/// whitespace (MySQL and SQL Server compare strings without trailing spaces, PostgreSQL does not, so the store refuses the ambiguity).</exception>
public interface IConversationService
{
    /// <summary>Opens the conversation of a subject, creating it, or reopening a closed one.</summary>
    /// <param name="actor">Who is acting.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OpenResult> OpenAsync(ConversationActor actor, string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default);

    /// <summary>Makes a member or partner agent (identified by <see cref="ConversationActor.UserRef"/>) a participant; idempotent.</summary>
    /// <param name="actor">The member or partner joining.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="displayName">The name other participants see (at most <see cref="ConversationOptions.MaxDisplayNameLength"/> units, no control or markup characters).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<JoinResult> JoinAsync(ConversationActor actor, string tenantId, Guid conversationId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Posts a message, idempotent on <see cref="PostMessage.ClientMessageId"/>.</summary>
    /// <param name="actor">The author.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, CancellationToken cancellationToken = default);

    /// <summary>Reads messages after <paramref name="afterSeq"/> in sequence order.</summary>
    /// <param name="actor">The reader.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="afterSeq">Return messages with a greater sequence number (0 for the start).</param>
    /// <param name="limit">The most messages to return; capped by <see cref="ConversationOptions.MaxListLimit"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="afterSeq"/> is negative or <paramref name="limit"/> is not positive.</exception>
    Task<MessagePage> ListAsync(ConversationActor actor, string tenantId, Guid conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default);

    /// <summary>Moves the actor's read marker up to <paramref name="seq"/>, never down and never past the conversation's last message.</summary>
    /// <param name="actor">The reader.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="seq">The sequence number read up to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seq"/> is negative.</exception>
    Task<MarkReadResult> MarkReadAsync(ConversationActor actor, string tenantId, Guid conversationId, long seq, CancellationToken cancellationToken = default);

    /// <summary>Closes the conversation and sets its retention date; idempotent.</summary>
    /// <param name="actor">Who closes it.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<CloseResult> CloseAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default);
}
