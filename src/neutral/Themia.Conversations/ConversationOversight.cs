using System.Data.Common;

namespace Themia.Conversations;

/// <summary>How <see cref="IConversationOversight.ReadAsync"/> ended.</summary>
public enum OversightReadOutcome
{
    /// <summary>The page was read and the read was recorded (the page may be empty).</summary>
    Listed = 0,

    /// <summary>The policy denied the call, the actor is not a member with a user reference, or the conversation is not readable (missing, another tenant's, or being purged).</summary>
    Refused = 1,

    /// <summary>No <see cref="IConversationReadAudit"/> is registered: nothing was read, because an unaudited read is not allowed.</summary>
    AuditPortMissing = 2,

    /// <summary>Recording the read failed: nothing is returned and nothing was recorded.</summary>
    AuditFailed = 3,
}

/// <summary>The result of <see cref="IConversationOversight.ReadAsync"/>.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Messages">The page, in sequence order; empty unless <see cref="Listed"/>.</param>
public sealed record OversightPage(OversightReadOutcome Outcome, IReadOnlyList<ConversationMessage> Messages)
{
    /// <summary>Whether the page was read and recorded.</summary>
    public bool Listed => Outcome == OversightReadOutcome.Listed;
}

/// <summary>What <see cref="IConversationReadAudit"/> is handed for one oversight read: ids, a count and a time, never message text, a name or a contact.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConversationId">The conversation that was read.</param>
/// <param name="ActorKind">The kind of the reading actor (always a member).</param>
/// <param name="ActorUserRef">The opaque user reference of the reader, in the app's namespace.</param>
/// <param name="Count">How many messages the page held (0 for an empty page: the room was still opened).</param>
/// <param name="ReadAt">When the read happened (the package's clock), so the port can bucket reads.</param>
public sealed record OversightRead(string TenantId, Guid ConversationId, ConversationActorKind ActorKind, string ActorUserRef, int Count, DateTimeOffset ReadAt);

/// <summary>
/// Staff reading a conversation they are not a participant of. Only a member with a user reference can do it, the policy must allow
/// <see cref="ConversationAction.OversightRead"/>, and every read is recorded through <see cref="IConversationReadAudit"/> in the same transaction as the read.
/// Bodies are shown as for a member participant (through the masker); the policy decides who may do this at all.
/// </summary>
public interface IConversationOversight
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> messages after <paramref name="afterSeq"/> and records the read. Does not make the actor a participant and does not move any read marker.
    /// A conversation under legal hold or past retention is readable; one being purged is not.
    /// </summary>
    /// <param name="actor">The reading member.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="afterSeq">Return messages with a greater sequence number.</param>
    /// <param name="limit">The page size (capped by <see cref="ConversationOptions.MaxListLimit"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OversightPage> ReadAsync(ConversationActor actor, string tenantId, Guid conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// The audit trail of oversight reads. No default is registered: without one, <see cref="IConversationOversight.ReadAsync"/> answers
/// <see cref="OversightReadOutcome.AuditPortMissing"/>. Called inside the read transaction, so what it writes commits with the read or not at all.
/// The app owns deduplication (for example one row per reader and conversation per 15 minutes, using <see cref="OversightRead.ReadAt"/>) and the retention of the trail.
/// </summary>
public interface IConversationReadAudit
{
    /// <summary>Records <paramref name="entry"/> on <paramref name="connection"/> within <paramref name="transaction"/>. An exception fails the read: nothing is returned.</summary>
    /// <param name="connection">The open connection of the read transaction.</param>
    /// <param name="transaction">The read transaction.</param>
    /// <param name="entry">The read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task RecordAsync(DbConnection connection, DbTransaction transaction, OversightRead entry, CancellationToken cancellationToken = default);
}
