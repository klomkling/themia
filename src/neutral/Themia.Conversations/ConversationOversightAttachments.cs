namespace Themia.Conversations;

/// <summary>How <see cref="IConversationOversightAttachments.OpenAsync"/> ended.</summary>
public enum OversightOpenOutcome
{
    /// <summary>The open was recorded and a short-lived URL came back.</summary>
    Opened = 0,

    /// <summary>The policy denied the call, the actor is not a member with a user reference, the conversation is not readable (missing, another tenant's, or being purged), or the attachment is not in it.</summary>
    Refused = 1,

    /// <summary>No <see cref="IConversationReadAudit"/> is registered: nothing was opened, because an unaudited open is not allowed.</summary>
    AuditPortMissing = 2,

    /// <summary>Recording the open failed: no URL was issued and nothing was recorded.</summary>
    AuditFailed = 3,
}

/// <summary>The result of <see cref="IConversationOversightAttachments.OpenAsync"/>.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Url">A bearer URL valid for <see cref="ConversationOptions.AttachmentDownloadLifetime"/>; treat it as a secret and never log it. Null unless <see cref="Opened"/>.</param>
/// <param name="Attachment">The attachment, for <see cref="OversightOpenOutcome.Opened"/>.</param>
public sealed record OversightOpenResult(OversightOpenOutcome Outcome, Uri? Url, AttachmentInfo? Attachment)
{
    /// <summary>Whether the open was recorded and a URL came back.</summary>
    public bool Opened => Outcome == OversightOpenOutcome.Opened;
}

/// <summary>
/// Staff opening a file of a conversation they are not a participant of. The same gate as <see cref="IConversationOversight"/>: the policy must allow
/// <see cref="ConversationAction.OversightRead"/>, the actor is a member with a user reference, and the open is recorded through <see cref="IConversationReadAudit"/>
/// (with <see cref="OversightRead.AttachmentId"/> set) in one transaction before any URL is issued. It does not make the actor a participant. A separate interface so
/// an adopter's fake of <see cref="IConversationAttachments"/> is untouched; registered by <c>AddThemiaConversationAttachments</c>.
/// </summary>
public interface IConversationOversightAttachments
{
    /// <summary>
    /// Records the open, then returns a short-lived download-only URL (served as a download, never inline). The file name is masked as it is for a member.
    /// A conversation under legal hold or past retention is readable; one being purged is not. Fails closed: no URL unless the record committed.
    /// </summary>
    /// <param name="actor">The opening member.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation the attachment belongs to.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OversightOpenResult> OpenAsync(ConversationActor actor, string tenantId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default);
}
