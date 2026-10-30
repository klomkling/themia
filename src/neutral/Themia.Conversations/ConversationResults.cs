namespace Themia.Conversations;

/// <summary>The conversation a subject is attached to: one subject, one conversation per tenant.</summary>
/// <param name="SubjectType">The kind of object, in the app's vocabulary (at most 100 characters, no leading or trailing whitespace).</param>
/// <param name="SubjectId">The object's id (at most 200 characters, no leading or trailing whitespace).</param>
public sealed record ConversationSubject(string SubjectType, string SubjectId);

/// <summary>A message to post.</summary>
/// <param name="ClientMessageId">Chosen by the sender and reused on a retry, so a retry never posts twice (at most 64 characters, no leading or trailing whitespace).</param>
/// <param name="Body">Plain text, at most <see cref="ConversationOptions.MaxBodyLength"/> UTF-16 units.</param>
public sealed record PostMessage(string ClientMessageId, string Body);

/// <summary>The result of <see cref="IConversationService.OpenAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="ConversationId">The conversation, except for <see cref="OpenOutcome.PendingPurge"/> and <see cref="OpenOutcome.Refused"/>.</param>
public sealed record OpenResult(OpenOutcome Outcome, Guid? ConversationId)
{
    /// <summary>Whether a usable, open conversation came back.</summary>
    public bool Succeeded => Outcome is OpenOutcome.Opened or OpenOutcome.Reopened;
}

/// <summary>The result of <see cref="IConversationService.JoinAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="ParticipantId">The participant, for <see cref="JoinOutcome.Joined"/> and <see cref="JoinOutcome.AlreadyJoined"/>.</param>
public sealed record JoinResult(JoinOutcome Outcome, Guid? ParticipantId)
{
    /// <summary>Whether the actor is a participant now.</summary>
    public bool Succeeded => Outcome is JoinOutcome.Joined or JoinOutcome.AlreadyJoined;
}

/// <summary>The result of <see cref="IConversationService.PostAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="MessageId">The stored message, for <see cref="PostOutcome.Posted"/> and <see cref="PostOutcome.Duplicate"/>.</param>
/// <param name="Seq">Its sequence number in the conversation.</param>
public sealed record PostResult(PostOutcome Outcome, Guid? MessageId, long? Seq)
{
    /// <summary>Whether the message is stored (now, or by an earlier identical call).</summary>
    public bool Succeeded => Outcome is PostOutcome.Posted or PostOutcome.Duplicate;
}

/// <summary>A stored message.</summary>
/// <param name="Id">The message id.</param>
/// <param name="Seq">Dense per conversation: the only ordering.</param>
/// <param name="ParticipantId">Who wrote it.</param>
/// <param name="Kind">Text or system line.</param>
/// <param name="Body">The text; <see langword="null"/> once redacted.</param>
/// <param name="CreatedAt">When it was stored (UTC).</param>
/// <param name="IsRedacted">Whether the body was removed by erasure or staff redaction.</param>
public sealed record ConversationMessage(Guid Id, long Seq, Guid ParticipantId, MessageKind Kind, string? Body, DateTimeOffset CreatedAt, bool IsRedacted);

/// <summary>The result of <see cref="IConversationService.ListAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Messages">The messages in sequence order; empty unless <see cref="ListOutcome.Listed"/>.</param>
public sealed record MessagePage(ListOutcome Outcome, IReadOnlyList<ConversationMessage> Messages);

/// <summary>The result of <see cref="IConversationService.MarkReadAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="LastReadSeq">The marker after the call; 0 when refused.</param>
public sealed record MarkReadResult(MarkReadOutcome Outcome, long LastReadSeq);

/// <summary>The result of <see cref="IConversationService.CloseAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
public sealed record CloseResult(CloseOutcome Outcome)
{
    /// <summary>Whether the conversation is closed now.</summary>
    public bool Succeeded => Outcome is CloseOutcome.Closed or CloseOutcome.AlreadyClosed;
}
