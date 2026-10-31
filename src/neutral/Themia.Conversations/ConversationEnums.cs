namespace Themia.Conversations;

/// <summary>Whether a conversation accepts new messages.</summary>
public enum ConversationState
{
    /// <summary>Accepts posts.</summary>
    Open = 0,

    /// <summary>Refuses posts; may be reopened until it is purged.</summary>
    Closed = 1,
}

/// <summary>Where a conversation stands against its retention date.</summary>
public enum ConversationRetentionState
{
    /// <summary>Within retention.</summary>
    Active = 0,

    /// <summary>Held: the purge skips it, and an erasure request is deferred.</summary>
    LegalHold = 1,

    /// <summary>Past its retention date and waiting for the purge run; it can no longer be opened.</summary>
    Expired = 2,

    /// <summary>A purge has claimed the conversation: its files are being deleted and its rows follow. It can no longer be reopened, joined or put on hold.</summary>
    Purging = 3,
}

/// <summary>What kind of party a participant is.</summary>
public enum ParticipantKind
{
    /// <summary>An identity user of the app (or an agent of another app arriving through the partner API).</summary>
    Member = 0,

    /// <summary>A person invited by contact, without an account.</summary>
    Guest = 1,

    /// <summary>The system itself, for system messages.</summary>
    System = 2,
}

/// <summary>What a message holds.</summary>
public enum MessageKind
{
    /// <summary>Plain text written by a participant.</summary>
    Text = 0,

    /// <summary>A line written by the system.</summary>
    System = 1,
}

/// <summary>Who is acting, as far as the access policy is concerned.</summary>
public enum ConversationActorKind
{
    /// <summary>A member of the app.</summary>
    Member = 0,

    /// <summary>A guest holding a verified guest session.</summary>
    Guest = 1,

    /// <summary>An agent of another app, arriving through the partner API.</summary>
    Partner = 2,

    /// <summary>The system.</summary>
    System = 3,
}

/// <summary>What an actor is asking to do to a conversation.</summary>
public enum ConversationAction
{
    /// <summary>Open (or create) the conversation of a subject.</summary>
    Open = 0,

    /// <summary>Read messages.</summary>
    Read = 1,

    /// <summary>Post a message.</summary>
    Post = 2,

    /// <summary>Move the actor's read marker.</summary>
    MarkRead = 3,

    /// <summary>Close or reopen the conversation.</summary>
    Close = 4,

    /// <summary>Invite a guest.</summary>
    Invite = 5,

    /// <summary>Export the transcript.</summary>
    Export = 6,

    /// <summary>Become a participant (a member or partner agent joining by its user reference).</summary>
    Join = 7,

    /// <summary>Place a legal hold on a conversation (<see cref="IConversationRetention.PlaceHoldAsync"/>).</summary>
    Hold = 8,

    /// <summary>Lift a legal hold (<see cref="IConversationRetention.ReleaseHoldAsync"/>).</summary>
    ReleaseHold = 9,

    /// <summary>Erase one person's data across conversations (<see cref="IConversationDataSubject.ErasePersonAsync"/>).</summary>
    Erase = 10,

    /// <summary>Read a conversation as staff without being a participant (<see cref="IConversationOversight.ReadAsync"/>). Allow it narrowly: every read is audited, but it shows message bodies.</summary>
    OversightRead = 11,
}

/// <summary>How <see cref="IConversationService.OpenAsync"/> ended.</summary>
public enum OpenOutcome
{
    /// <summary>The subject's conversation was created, or already open.</summary>
    Opened = 0,

    /// <summary>A closed conversation was reopened and its retention date cleared.</summary>
    Reopened = 1,

    /// <summary>The conversation is past its retention date and waits for the purge; it cannot be opened until then.</summary>
    PendingPurge = 2,

    /// <summary>The access policy denied the call.</summary>
    Refused = 3,
}

/// <summary>How <see cref="IConversationService.JoinAsync"/> ended.</summary>
public enum JoinOutcome
{
    /// <summary>The actor became a participant.</summary>
    Joined = 0,

    /// <summary>The actor already was one.</summary>
    AlreadyJoined = 1,

    /// <summary>The display name is empty, too long, or holds control or markup characters.</summary>
    Invalid = 2,

    /// <summary>The policy denied the call, the conversation does not exist in this tenant, or the actor has no user reference.</summary>
    Refused = 3,
}

/// <summary>How <see cref="IConversationService.PostAsync"/> ended.</summary>
public enum PostOutcome
{
    /// <summary>The message was stored with a new sequence number.</summary>
    Posted = 0,

    /// <summary>The same client message id was posted before; the original message is returned and nothing is overwritten.</summary>
    Duplicate = 1,

    /// <summary>The conversation is closed.</summary>
    Closed = 2,

    /// <summary>The body is empty or longer than <see cref="ConversationOptions.MaxBodyLength"/>.</summary>
    InvalidMessage = 3,

    /// <summary>The policy denied the call, or the actor is not an active participant of this conversation in this tenant.</summary>
    Refused = 4,
}

/// <summary>How <see cref="IConversationService.ListAsync"/> ended.</summary>
public enum ListOutcome
{
    /// <summary>The page was read (it may be empty).</summary>
    Listed = 0,

    /// <summary>The policy denied the call, or the actor is not an active participant.</summary>
    Refused = 1,
}

/// <summary>How <see cref="IConversationService.MarkReadAsync"/> ended.</summary>
public enum MarkReadOutcome
{
    /// <summary>The marker is at <see cref="MarkReadResult.LastReadSeq"/> (unchanged when the request was not ahead of it).</summary>
    Marked = 0,

    /// <summary>The policy denied the call, or the actor is not an active participant.</summary>
    Refused = 1,
}

/// <summary>How <see cref="IConversationService.CloseAsync"/> ended.</summary>
public enum CloseOutcome
{
    /// <summary>The conversation was open and is now closed.</summary>
    Closed = 0,

    /// <summary>The conversation was closed already; nothing changed.</summary>
    AlreadyClosed = 1,

    /// <summary>The policy denied the call, or the actor is not an active participant.</summary>
    Refused = 2,
}

/// <summary>What a <see cref="ConversationEvent"/> reports.</summary>
public enum ConversationEventKind
{
    /// <summary>A message was posted.</summary>
    MessagePosted = 0,
}
