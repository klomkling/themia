namespace Themia.Conversations;

/// <summary>The limits and retention of the conversation store. Every cap has a default; <see cref="Validate"/> runs at registration, so a nonsense value fails at startup.</summary>
public sealed class ConversationOptions
{
    /// <summary>The widest <see cref="MaxBodyLength"/> the schema holds (the <c>body</c> column is 4,000 UTF-16 units).</summary>
    public const int BodyColumnWidth = 4000;

    /// <summary>The widest <see cref="MaxDisplayNameLength"/> the schema holds.</summary>
    public const int DisplayNameColumnWidth = 80;

    /// <summary>The longest message body, in <see cref="string.Length"/> (UTF-16 units; that fits <c>NVARCHAR(4000)</c> and MySQL <c>VARCHAR(4000)</c>). Default 4,000.</summary>
    public int MaxBodyLength { get; set; } = BodyColumnWidth;

    /// <summary>The longest participant display name, in UTF-16 units, with no control or markup characters. Default 80.</summary>
    public int MaxDisplayNameLength { get; set; } = DisplayNameColumnWidth;

    /// <summary>Attachments allowed on one message. Default 10.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 10;

    /// <summary>Total bytes of the attachments of one message. Default 10 MiB.</summary>
    public long MaxAttachmentBytesPerMessage { get; set; } = 10L * 1024 * 1024;

    /// <summary>Total bytes of the attachments of one conversation. Default 200 MiB.</summary>
    public long MaxAttachmentBytesPerConversation { get; set; } = 200L * 1024 * 1024;

    /// <summary>The most messages one list call returns, whatever the caller asks for. Default 100.</summary>
    public int MaxListLimit { get; set; } = 100;

    /// <summary>
    /// How long a conversation is kept after its subject closes, unless <see cref="RetentionBySubjectType"/> says otherwise. Default 180 days,
    /// <b>pending counsel review</b> of the legal basis: do not treat the number as decided.
    /// </summary>
    public TimeSpan DefaultRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>Retention per <c>SubjectType</c> (matched exactly, case-sensitive); a subject type not listed uses <see cref="DefaultRetention"/>.</summary>
    public IDictionary<string, TimeSpan> RetentionBySubjectType { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    /// <summary>The retention that applies to <paramref name="subjectType"/>.</summary>
    /// <param name="subjectType">The subject type of a conversation.</param>
    public TimeSpan RetentionFor(string subjectType) =>
        RetentionBySubjectType.TryGetValue(subjectType, out var retention) ? retention : DefaultRetention;

    internal void Validate()
    {
        Require(MaxBodyLength is > 0 and <= BodyColumnWidth, nameof(MaxBodyLength), $"must be between 1 and {BodyColumnWidth}, the width of the body column");
        Require(MaxDisplayNameLength is > 0 and <= DisplayNameColumnWidth, nameof(MaxDisplayNameLength), $"must be between 1 and {DisplayNameColumnWidth}, the width of the column");
        Require(MaxAttachmentsPerMessage > 0, nameof(MaxAttachmentsPerMessage), "must be positive");
        Require(MaxAttachmentBytesPerMessage > 0, nameof(MaxAttachmentBytesPerMessage), "must be positive");
        Require(MaxAttachmentBytesPerConversation >= MaxAttachmentBytesPerMessage, nameof(MaxAttachmentBytesPerConversation), "must not be smaller than the per-message cap");
        Require(MaxListLimit > 0, nameof(MaxListLimit), "must be positive");
        Require(DefaultRetention > TimeSpan.Zero, nameof(DefaultRetention), "must be positive");
        foreach (var (subjectType, retention) in RetentionBySubjectType)
        {
            Require(retention > TimeSpan.Zero, nameof(RetentionBySubjectType), $"the retention of '{subjectType}' must be positive");
        }
    }

    private static void Require(bool condition, string option, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{nameof(ConversationOptions)}.{option} {message}.");
        }
    }
}
