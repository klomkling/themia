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
}
