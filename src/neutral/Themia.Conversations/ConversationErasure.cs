namespace Themia.Conversations;

/// <summary>A person whose data is erased: a member (or partner) by user reference, or a guest by the contact they were invited with.</summary>
public sealed record PersonRef
{
    private PersonRef(ConversationActorKind kind, string? userRef, GuestContact? contact)
    {
        Kind = kind;
        UserRef = userRef;
        Contact = contact;
    }

    /// <summary>Whether the person is a member, a partner or a guest.</summary>
    public ConversationActorKind Kind { get; }

    /// <summary>The user reference, for a member or partner.</summary>
    public string? UserRef { get; }

    /// <summary>The invite contact, for a guest.</summary>
    public GuestContact? Contact { get; }

    /// <summary>A member of the tenant's staff.</summary>
    /// <param name="userRef">The same user reference <see cref="ConversationActor"/> carries.</param>
    /// <returns>The person.</returns>
    public static PersonRef ForMember(string userRef) => new(ConversationActorKind.Member, userRef, null);

    /// <summary>An outside partner with a user reference.</summary>
    /// <param name="userRef">The same user reference <see cref="ConversationActor"/> carries.</param>
    /// <returns>The person.</returns>
    public static PersonRef ForPartner(string userRef) => new(ConversationActorKind.Partner, userRef, null);

    /// <summary>A guest, found by the hash of the contact they were invited with (every configured pepper is tried).</summary>
    /// <param name="contact">The contact the guest was invited with.</param>
    /// <returns>The person.</returns>
    public static PersonRef ForGuest(GuestContact contact) => new(ConversationActorKind.Guest, null, contact);
}

/// <summary>How an erasure ended.</summary>
public enum ErasureOutcome
{
    /// <summary>Everything found was erased, or the person has no data here (the call is idempotent).</summary>
    Erased = 0,

    /// <summary>Some conversations were erased and others were deferred by a hold or failed.</summary>
    PartiallyErased = 1,

    /// <summary>Nothing was erased: every conversation found is on hold or failed.</summary>
    Deferred = 2,

    /// <summary>The access policy refused <see cref="ConversationAction.Erase"/>.</summary>
    Refused = 3,

    /// <summary>The person reference is not valid, or a guest was named but guest access is not configured.</summary>
    Invalid = 4,
}

/// <summary>The result of <see cref="IConversationDataSubject.ErasePersonAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Conversations">Conversations in which the person's data was erased.</param>
/// <param name="MessagesRedacted">Messages whose body was removed.</param>
/// <param name="AttachmentsDeleted">Attachments whose file and row were deleted.</param>
/// <param name="DeferredConversationIds">Conversations skipped because they are under legal hold or being purged; ask again after the hold is lifted.</param>
/// <param name="FailedConversationIds">Conversations left untouched because a file could not be deleted; ask again.</param>
public sealed record ErasureResult(
    ErasureOutcome Outcome,
    int Conversations,
    int MessagesRedacted,
    int AttachmentsDeleted,
    IReadOnlyList<Guid> DeferredConversationIds,
    IReadOnlyList<Guid> FailedConversationIds)
{
    /// <summary>Whether nothing the person owns is left to erase.</summary>
    public bool Complete => Outcome == ErasureOutcome.Erased;
}

/// <summary>How <see cref="IConversationDataSubject.PurgeConversationAsync(ConversationActor, string, Guid, CancellationToken)"/> ended.</summary>
public enum ConversationPurgeOutcome
{
    /// <summary>The conversation, its rows and every file were deleted by this call.</summary>
    Purged = 0,

    /// <summary>No such conversation: it never existed or an earlier erase (or a retention purge) already deleted it. Safe to treat as done.</summary>
    NotFound = 1,

    /// <summary>Nothing was deleted: the conversation is under legal hold, or a recent upload may still be arriving. Ask again later.</summary>
    Deferred = 2,

    /// <summary>A file could not be deleted (or no <c>IStorageProvider</c> is registered) or the database failed. Rows are kept and the conversation stays claimed for deletion; ask again.</summary>
    Failed = 3,

    /// <summary>The access policy refused <see cref="ConversationAction.PurgeConversation"/>.</summary>
    Refused = 4,
}

/// <summary>The result of <see cref="IConversationDataSubject.PurgeConversationAsync(ConversationActor, string, Guid, CancellationToken)"/>. Counts only: no text, file name or participant.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="MessagesDeleted">Messages deleted; for <see cref="ConversationPurgeOutcome.NotFound"/> the count the earlier purge recorded, or 0 when none is known.</param>
/// <param name="AttachmentsDeleted">Attachments deleted; same rule as <paramref name="MessagesDeleted"/>.</param>
public sealed record ConversationPurgeResult(ConversationPurgeOutcome Outcome, int MessagesDeleted, int AttachmentsDeleted)
{
    /// <summary>Whether nothing of the conversation is left: it was erased now or earlier.</summary>
    public bool Complete => Outcome is ConversationPurgeOutcome.Purged or ConversationPurgeOutcome.NotFound;

    /// <summary>Whether nothing was deleted because of a legal hold or an upload still arriving; ask again later.</summary>
    public bool Held => Outcome == ConversationPurgeOutcome.Deferred;

    /// <summary>Whether a file or the database failed; the conversation stays claimed and a retry resumes.</summary>
    public bool Failed => Outcome == ConversationPurgeOutcome.Failed;
}

/// <summary>Data-subject requests: erase one person's data across the tenant's conversations while the other participants' history keeps its shape.</summary>
public interface IConversationDataSubject
{
    /// <summary>Redacts the person's messages, anonymises their participant rows, deletes their attachments and revokes their guest sessions. Idempotent.</summary>
    /// <param name="actor">The staff or DSR actor; the access policy decides with <see cref="ConversationAction.Erase"/>.</param>
    /// <param name="tenantId">The tenant; nothing in another tenant is touched.</param>
    /// <param name="person">The person.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was erased, and what was deferred or failed.</returns>
    Task<ErasureResult> ErasePersonAsync(ConversationActor actor, string tenantId, PersonRef person, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the attachments <see cref="ErasePersonAsync"/> would delete for the same person: the files they own plus the files attached to a message they posted
    /// (metadata only, no bytes, no audit event; the caller records the export). The set is the erase predicate itself, so an export and an erasure cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>Each conversation is read on its own, so the result is a point in time per conversation, not across the tenant. A conversation under legal hold is listed
    /// (reading is allowed) although erasure defers it. A listed <see cref="PersonAttachment.StorageKey"/> may no longer resolve: erasure deletes files before its row
    /// transaction, and a hold or purge that lands in between leaves rows whose file is gone. The caller reports "file no longer available" for that one file.</para>
    /// <para><see cref="PersonAttachment.FileName"/> is empty for a row written before file names existed. Staging uploads that never became an attachment are not listed
    /// (erasure deletes them, but they were never posted). A person already erased has no participant row left to find, so the result is <see cref="PersonAttachmentsOutcome.Listed"/> with no items.</para>
    /// </remarks>
    /// <param name="actor">The staff or DSR actor; the access policy decides with <see cref="ConversationAction.ListPersonData"/>.</param>
    /// <param name="tenantId">The tenant; nothing in another tenant is read.</param>
    /// <param name="person">The person.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The attachments, ordered by conversation, message sequence and attachment id; or why nothing was listed.</returns>
    Task<PersonAttachmentsResult> ListPersonAttachmentsAsync(ConversationActor actor, string tenantId, PersonRef person, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one whole conversation: messages, participants, read markers, events, every attachment row and its stored file, and writes a tombstone with the counts.
    /// The same code path as a retention purge. Idempotent: a conversation that is gone answers <see cref="ConversationPurgeOutcome.NotFound"/> with the tombstone's counts.
    /// </summary>
    /// <remarks>
    /// A conversation under legal hold (the <see cref="ILegalHoldPort"/> is asked every time) or with a recent upload in flight is <see cref="ConversationPurgeOutcome.Deferred"/>.
    /// After a file failure the conversation stays in the purge state (no post, join or hold), the rows stay, and the same call finishes the job. The tombstone does not name the actor:
    /// record who asked in your own audit.
    /// </remarks>
    /// <param name="actor">The actor; the access policy decides with <see cref="ConversationAction.PurgeConversation"/>.</param>
    /// <param name="tenantId">The tenant; nothing in another tenant is touched.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How it ended, and the counts.</returns>
    Task<ConversationPurgeResult> PurgeConversationAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="PurgeConversationAsync"/>, addressed by subject. Never creates a conversation: a subject with none is <see cref="ConversationPurgeOutcome.NotFound"/>.</summary>
    /// <param name="actor">The actor; the access policy decides with <see cref="ConversationAction.PurgeConversation"/>.</param>
    /// <param name="tenantId">The tenant; nothing in another tenant is touched.</param>
    /// <param name="subject">The conversation's subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How it ended, and the counts.</returns>
    Task<ConversationPurgeResult> PurgeConversationBySubjectAsync(ConversationActor actor, string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default);
}
