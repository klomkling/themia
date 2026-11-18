namespace Themia.Conversations;

/// <summary>How <see cref="IConversationDataSubject.ListPersonAttachmentsAsync"/> ended.</summary>
public enum PersonAttachmentsOutcome
{
    /// <summary>The attachments found were listed; none is a valid answer (the person has no files here, or was already erased).</summary>
    Listed = 0,

    /// <summary>The access policy refused <see cref="ConversationAction.ListPersonData"/>; the list is empty because nothing was read, not because there are no files.</summary>
    Refused = 1,

    /// <summary>The person reference is not valid, or a guest was named but guest access is not configured.</summary>
    Invalid = 2,
}

/// <summary>One attachment a person owns or sent. Metadata only.</summary>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="AttachmentId">The attachment id.</param>
/// <param name="MessageSeq">The sequence number of the message it is attached to.</param>
/// <param name="FileName">The name the file was uploaded under; empty for a row written before file names were recorded.</param>
/// <param name="ContentType">The content type.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Sha256">The SHA-256 of the content, as hex.</param>
/// <param name="StorageKey">The internal key for <c>IStorageProvider</c>; not a URL, and not for display to the data subject.</param>
public sealed record PersonAttachment(
    Guid ConversationId,
    Guid AttachmentId,
    long MessageSeq,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    string StorageKey);

/// <summary>The result of <see cref="IConversationDataSubject.ListPersonAttachmentsAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Attachments">The attachments; empty, never <see langword="null"/>, unless <paramref name="Outcome"/> is <see cref="PersonAttachmentsOutcome.Listed"/> with files found.</param>
public sealed record PersonAttachmentsResult(PersonAttachmentsOutcome Outcome, IReadOnlyList<PersonAttachment> Attachments)
{
    /// <summary>Whether the call was allowed and the reference valid, so an empty list means the person has no files.</summary>
    public bool Listed => Outcome == PersonAttachmentsOutcome.Listed;
}
