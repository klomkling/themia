namespace Themia.Conversations;

/// <summary>A file to attach to a message.</summary>
/// <param name="FileName">The name the file is offered under. Path parts, control characters and markup are removed; at most 255 characters remain.</param>
/// <param name="ContentType">The declared type. It must be in <see cref="ConversationOptions.AllowedAttachmentContentTypes"/> and the bytes must look like it.</param>
/// <param name="Content">The bytes, read to the end. At most <see cref="ConversationOptions.MaxAttachmentBytesPerMessage"/> in total across the message.</param>
public sealed record AttachmentUpload(string FileName, string ContentType, Stream Content);

/// <summary>What a message listing says about an attachment. The bytes come from <see cref="IConversationAttachments.OpenAsync"/>.</summary>
/// <param name="Id">The attachment id.</param>
/// <param name="FileName">The sanitized name.</param>
/// <param name="ContentType">The stored type (an image is re-encoded, so this is the encoded type, not the declared one).</param>
/// <param name="Size">The stored size in bytes.</param>
public sealed record AttachmentInfo(Guid Id, string FileName, string ContentType, long Size);

/// <summary>How <see cref="IConversationAttachments.OpenAsync"/> ended.</summary>
public enum OpenAttachmentOutcome
{
    /// <summary>A short-lived URL came back.</summary>
    Opened = 0,

    /// <summary>The actor may not read this conversation, is not a participant, or the attachment is not in it.</summary>
    Refused = 1,
}

/// <summary>The result of <see cref="IConversationAttachments.OpenAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Url">A bearer URL valid for <see cref="ConversationOptions.AttachmentDownloadLifetime"/>; treat it as a secret and never log it.</param>
/// <param name="Attachment">The attachment, for <see cref="OpenAttachmentOutcome.Opened"/>.</param>
public sealed record OpenAttachmentResult(OpenAttachmentOutcome Outcome, Uri? Url, AttachmentInfo? Attachment)
{
    /// <summary>Whether a URL came back.</summary>
    public bool Succeeded => Outcome == OpenAttachmentOutcome.Opened;
}

/// <summary>
/// Posting with files and downloading them. A separate interface so an adopter's fake of <see cref="IConversationService"/> is untouched. Register it with
/// <c>AddThemiaConversationAttachments</c>, which needs an <c>IStorageProvider</c> and a Themia.Imaging <c>IImageProcessor</c> at startup.
/// </summary>
public interface IConversationAttachments
{
    /// <summary>
    /// Posts a message with files, idempotent on <see cref="PostMessage.ClientMessageId"/>. The body may be empty when at least one file is attached. Images are re-encoded (metadata
    /// is dropped); the declared type must be allowed and match the bytes. Nothing is stored unless the message is.
    /// </summary>
    /// <param name="actor">Who is posting.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="message">The message.</param>
    /// <param name="files">The files, at most <see cref="ConversationOptions.MaxAttachmentsPerMessage"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, IReadOnlyList<AttachmentUpload> files, CancellationToken cancellationToken = default);

    /// <summary>A short-lived download URL for an attachment, served as a download (never inline).</summary>
    /// <param name="actor">Who is reading.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation the attachment belongs to.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<OpenAttachmentResult> OpenAsync(ConversationActor actor, string tenantId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default);
}
