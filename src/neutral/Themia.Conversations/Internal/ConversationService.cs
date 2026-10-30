using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// The conversation store over Dapper. The SQL is portable except for the counter and paging, which the <see cref="IConversationDialect"/> owns. Logging
/// carries ids and outcomes only: never a body, a display name, a contact hash or a client message id.
/// </summary>
internal sealed partial class ConversationService(
    IConversationDialect dialect,
    ConversationOptions options,
    IConversationAccessPolicy policy,
    IConversationEventSink sink,
    IConversationMasker masker,
    TimeProvider time,
    ILogger<ConversationService> logger) : IConversationService
{
    private const int MaxOpenAttempts = 3;
    private const string MaskFailedBody = "[message hidden]";

    /// <inheritdoc />
    public async Task<OpenResult> OpenAsync(ConversationActor actor, string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ConversationInput.Subject(subject);

        if (!await AllowedAsync(actor, tenantId, null, ConversationAction.Open, cancellationToken, subject))
        {
            return new OpenResult(OpenOutcome.Refused, null);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        for (var attempt = 0; attempt < MaxOpenAttempts; attempt++)
        {
            // Autocommit: a unique violation inside a PostgreSQL transaction would abort it, and the re-read below must not need one.
            var id = Guid.NewGuid();
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO conversations (id, tenant_id, subject_type, subject_id, state, retention_state, last_seq, created_at) VALUES (@Id, @TenantId, @SubjectType, @SubjectId, 0, 0, 0, @Now);",
                    new { Id = id, TenantId = tenantId, subject.SubjectType, subject.SubjectId, Now = Now() },
                    cancellationToken: cancellationToken));
                return new OpenResult(OpenOutcome.Opened, id);
            }
            catch (DbException ex) when (dialect.IsDuplicateKey(ex))
            {
            }

            var existing = await connection.QuerySingleOrDefaultAsync<ExistingConversation>(new CommandDefinition(
                "SELECT id AS Id, state AS State, retention_state AS RetentionState, retention_until AS RetentionUntil FROM conversations WHERE tenant_id = @TenantId AND subject_type = @SubjectType AND subject_id = @SubjectId;",
                new { TenantId = tenantId, subject.SubjectType, subject.SubjectId },
                cancellationToken: cancellationToken));
            if (existing is null)
            {
                continue; // purged between the insert and the read: try again
            }

            // The first check could not name a conversation. Ask again with the real id before revealing it or reopening it.
            if (!await AllowedAsync(actor, tenantId, existing.Id, ConversationAction.Open, cancellationToken, subject))
            {
                return new OpenResult(OpenOutcome.Refused, null);
            }

            if (existing.IsPastRetention(time.GetUtcNow()))
            {
                return new OpenResult(OpenOutcome.PendingPurge, null);
            }

            if (existing.State == (int)ConversationState.Open)
            {
                return new OpenResult(OpenOutcome.Opened, existing.Id);
            }

            var reopened = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE conversations SET state = 0, closed_at = NULL, retention_until = NULL WHERE tenant_id = @TenantId AND id = @Id AND state = 1 AND retention_state <> 2 AND (retention_until IS NULL OR retention_until > @Now);",
                new { TenantId = tenantId, existing.Id, Now = Now() },
                cancellationToken: cancellationToken));
            if (reopened == 1)
            {
                return new OpenResult(OpenOutcome.Reopened, existing.Id);
            }
        }

        throw new InvalidOperationException("The conversation of the subject changed state faster than it could be opened; try again.");
    }

    /// <inheritdoc />
    public async Task<JoinResult> JoinAsync(ConversationActor actor, string tenantId, Guid conversationId, string displayName, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));

        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Join, cancellationToken)
            || actor.Kind is not (ConversationActorKind.Member or ConversationActorKind.Partner)
            || actor.UserRef is null)
        {
            return new JoinResult(JoinOutcome.Refused, null);
        }

        if (!ConversationInput.IsDisplayName(displayName, options.MaxDisplayNameLength))
        {
            return new JoinResult(JoinOutcome.Invalid, null);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var participantId = Guid.NewGuid();
        try
        {
            // INSERT ... SELECT, so a conversation that is not this tenant's inserts nothing and answers Refused like a missing one.
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO conversation_participants (id, tenant_id, conversation_id, kind, user_ref, display_name, joined_at, last_read_seq) " +
                "SELECT @Id, @TenantId, c.id, 0, @UserRef, @DisplayName, @Now, 0 FROM conversations c WHERE c.tenant_id = @TenantId AND c.id = @ConversationId;",
                new { Id = participantId, TenantId = tenantId, ConversationId = conversationId, UserRef = ConversationInput.StoredUserRef(actor), DisplayName = displayName, Now = Now() },
                cancellationToken: cancellationToken));
            return inserted == 1 ? new JoinResult(JoinOutcome.Joined, participantId) : new JoinResult(JoinOutcome.Refused, null);
        }
        catch (DbException ex) when (dialect.IsDuplicateKey(ex))
        {
            var existing = await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken);
            return existing is { } id ? new JoinResult(JoinOutcome.AlreadyJoined, id) : new JoinResult(JoinOutcome.Refused, null);
        }
    }

    /// <inheritdoc />
    public async Task<MessagePage> ListAsync(ConversationActor actor, string tenantId, Guid conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var refused = new MessagePage(ListOutcome.Refused, []);
        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Read, cancellationToken))
        {
            return refused;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is null)
        {
            return refused;
        }

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            "SELECT m.id AS Id, m.seq AS Seq, m.participant_id AS ParticipantId, m.kind AS Kind, m.body AS Body, m.created_at AS CreatedAt, m.redacted_at AS RedactedAt, " +
            "p.kind AS SenderKind, p.display_name AS SenderName, p.role_label AS SenderRole " +
            "FROM conversation_messages m LEFT JOIN conversation_participants p ON p.tenant_id = m.tenant_id AND p.conversation_id = m.conversation_id AND p.id = m.participant_id " +
            "WHERE m.tenant_id = @TenantId AND m.conversation_id = @ConversationId AND m.seq > @AfterSeq ORDER BY m.seq " + dialect.LimitClause + ";",
            new { TenantId = tenantId, ConversationId = conversationId, AfterSeq = afterSeq, Limit = Math.Min(limit, options.MaxListLimit) },
            cancellationToken: cancellationToken));
        return new MessagePage(ListOutcome.Listed, rows.Select(r => ViewOf(actor.Kind, r.ToMessage(actor.Kind), conversationId)).ToList());
    }

    /// <summary>The message as the viewer may see it. A masker that throws (or times out) hides the body: fail closed, never the raw text.</summary>
    private ConversationMessage ViewOf(ConversationActorKind viewer, ConversationMessage message, Guid conversationId)
    {
        try
        {
            return masker.Mask(viewer, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Conversation {ConversationId} message {MessageId} could not be masked for a {Viewer} viewer; its body is hidden", conversationId, message.Id, viewer);
            return message with { Body = message.IsRedacted ? null : MaskFailedBody };
        }
    }

    /// <inheritdoc />
    public async Task<MarkReadResult> MarkReadAsync(ConversationActor actor, string tenantId, Guid conversationId, long seq, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentOutOfRangeException.ThrowIfNegative(seq);

        var refused = new MarkReadResult(MarkReadOutcome.Refused, 0);
        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.MarkRead, cancellationToken))
        {
            return refused;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is not { } participantId)
        {
            return refused;
        }

        // last_seq only grows, so clamping to a value read a moment ago can never exceed the true last message.
        var lastSeq = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT last_seq FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;",
            new { TenantId = tenantId, ConversationId = conversationId },
            cancellationToken: cancellationToken));
        var parameters = new { TenantId = tenantId, ConversationId = conversationId, ParticipantId = participantId, Target = Math.Min(seq, lastSeq) };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_participants SET last_read_seq = CASE WHEN last_read_seq < @Target THEN @Target ELSE last_read_seq END WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND id = @ParticipantId;",
            parameters,
            cancellationToken: cancellationToken));
        var marker = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT last_read_seq FROM conversation_participants WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND id = @ParticipantId;",
            parameters,
            cancellationToken: cancellationToken));
        return new MarkReadResult(MarkReadOutcome.Marked, marker);
    }

    /// <inheritdoc />
    public async Task<CloseResult> CloseAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));

        var refused = new CloseResult(CloseOutcome.Refused);
        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Close, cancellationToken))
        {
            return refused;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is null)
        {
            return refused;
        }

        var subjectType = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT subject_type FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;",
            new { TenantId = tenantId, ConversationId = conversationId },
            cancellationToken: cancellationToken));
        var now = time.GetUtcNow();
        var closed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET state = 1, closed_at = @Now, retention_until = @Until WHERE tenant_id = @TenantId AND id = @ConversationId AND state = 0;",
            new { TenantId = tenantId, ConversationId = conversationId, Now = now.UtcDateTime, Until = (now + options.RetentionFor(subjectType!)).UtcDateTime },
            cancellationToken: cancellationToken));
        return new CloseResult(closed == 1 ? CloseOutcome.Closed : CloseOutcome.AlreadyClosed);
    }

    internal DateTime Now() => time.GetUtcNow().UtcDateTime;

    internal async ValueTask<bool> AllowedAsync(ConversationActor actor, string tenantId, Guid? conversationId, ConversationAction action, CancellationToken cancellationToken, ConversationSubject? subject = null) =>
        await policy.CanAsync(actor, new ConversationTarget(tenantId, conversationId, subject), action, cancellationToken);

    internal async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = dialect.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The participant this actor is, in this tenant and conversation, if still active and of the matching kind. A guest is proved by the hash of the session key it
    /// holds (this tenant and conversation, unexpired, not revoked, participant not left) in the same statement; no id the caller supplies is trusted.
    /// </summary>
    internal async Task<Guid?> ResolveParticipantAsync(DbConnection connection, DbTransaction? transaction, ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        const string Active = "tenant_id = @TenantId AND conversation_id = @ConversationId AND left_at IS NULL";
        var sql = actor switch
        {
            { Kind: ConversationActorKind.Member or ConversationActorKind.Partner, UserRef: not null } =>
                $"SELECT id FROM conversation_participants WHERE {Active} AND kind = 0 AND user_ref = @UserRef;",
            { Kind: ConversationActorKind.Guest, GuestSession: not null } =>
                "SELECT p.id FROM conversation_guest_sessions s JOIN conversation_participants p ON p.tenant_id = s.tenant_id AND p.conversation_id = s.conversation_id AND p.id = s.participant_id " +
                "WHERE s.key_hash = @KeyHash AND s.tenant_id = @TenantId AND s.conversation_id = @ConversationId AND s.revoked_at IS NULL AND s.expires_at > @Now AND p.left_at IS NULL AND p.kind = 1;",
            _ => null,
        };

        // Dapper sends only the parameters the statement names.
        return sql is null
            ? null
            : await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                sql,
                new { TenantId = tenantId, ConversationId = conversationId, UserRef = ConversationInput.StoredUserRef(actor), KeyHash = actor.GuestSession?.Hash(), Now = Now() },
                transaction,
                cancellationToken: cancellationToken));
    }

    private sealed class ExistingConversation
    {
        public Guid Id { get; set; }

        public int State { get; set; }

        public int RetentionState { get; set; }

        public object? RetentionUntil { get; set; }

        public bool IsPastRetention(DateTimeOffset now) =>
            RetentionState == (int)ConversationRetentionState.Expired || (RetentionUntil is not null && ConversationTime.ToUtc(RetentionUntil) <= now);
    }

    private sealed class MessageRow
    {
        public Guid Id { get; set; }

        public long Seq { get; set; }

        public Guid ParticipantId { get; set; }

        public int Kind { get; set; }

        public string? Body { get; set; }

        public object CreatedAt { get; set; } = null!;

        public object? RedactedAt { get; set; }

        public int? SenderKind { get; set; }

        public string? SenderName { get; set; }

        public string? SenderRole { get; set; }

        /// <summary>A guest sees staff by role label, never by name; everyone else sees the display name.</summary>
        private string? LabelFor(ConversationActorKind viewer) => SenderKind switch
        {
            (int)ParticipantKind.System or null => "System",
            (int)ParticipantKind.Member when viewer == ConversationActorKind.Guest => string.IsNullOrWhiteSpace(SenderRole) ? "Staff" : SenderRole,
            _ => SenderName,
        };

        public ConversationMessage ToMessage(ConversationActorKind viewer) => new(
            Id,
            Seq,
            ParticipantId,
            Kind switch
            {
                (int)MessageKind.Text => MessageKind.Text,
                (int)MessageKind.System => MessageKind.System,
                _ => throw new InvalidOperationException($"Unknown message kind {Kind} stored for message {Id}."),
            },
            Body,
            ConversationTime.ToUtc(CreatedAt),
            RedactedAt is not null,
            LabelFor(viewer));
    }
}
