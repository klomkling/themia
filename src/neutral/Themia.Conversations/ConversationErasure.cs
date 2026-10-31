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
}
