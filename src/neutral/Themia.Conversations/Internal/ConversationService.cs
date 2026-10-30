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
    TimeProvider time,
    ILogger<ConversationService> logger) : IConversationService
{
    private const int MaxOpenAttempts = 3;

    /// <inheritdoc />
    public async Task<OpenResult> OpenAsync(ConversationActor actor, string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ConversationInput.Subject(subject);

        if (!await AllowedAsync(actor, tenantId, null, ConversationAction.Open, cancellationToken))
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
                new { Id = participantId, TenantId = tenantId, ConversationId = conversationId, actor.UserRef, DisplayName = displayName, Now = Now() },
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
            "SELECT id AS Id, seq AS Seq, participant_id AS ParticipantId, kind AS Kind, body AS Body, created_at AS CreatedAt, redacted_at AS RedactedAt " +
            "FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND seq > @AfterSeq ORDER BY seq " + dialect.LimitClause + ";",
            new { TenantId = tenantId, ConversationId = conversationId, AfterSeq = afterSeq, Limit = Math.Min(limit, options.MaxListLimit) },
            cancellationToken: cancellationToken));
        return new MessagePage(ListOutcome.Listed, rows.Select(r => r.ToMessage()).ToList());
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

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private async ValueTask<bool> AllowedAsync(ConversationActor actor, string tenantId, Guid? conversationId, ConversationAction action, CancellationToken cancellationToken) =>
        await policy.CanAsync(actor, new ConversationTarget(tenantId, conversationId), action, cancellationToken);

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
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
    /// The participant this actor is, in this tenant and conversation, if still active and of the matching kind. The id a guest actor carries is only a
    /// lookup key here, never an assertion: a member's participant id offered as a guest matches nothing.
    /// </summary>
    private static async Task<Guid?> ResolveParticipantAsync(DbConnection connection, DbTransaction? transaction, ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        const string Active = "tenant_id = @TenantId AND conversation_id = @ConversationId AND left_at IS NULL";
        var sql = actor switch
        {
            { Kind: ConversationActorKind.Member or ConversationActorKind.Partner, UserRef: not null } =>
                $"SELECT id FROM conversation_participants WHERE {Active} AND kind = 0 AND user_ref = @UserRef;",
            { Kind: ConversationActorKind.Guest, ParticipantId: not null } =>
                $"SELECT id FROM conversation_participants WHERE {Active} AND kind = 1 AND id = @ParticipantId;",
            _ => null,
        };

        // Dapper sends only the parameters the statement names.
        return sql is null
            ? null
            : await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                sql,
                new { TenantId = tenantId, ConversationId = conversationId, actor.UserRef, actor.ParticipantId },
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

        public ConversationMessage ToMessage() => new(
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
            RedactedAt is not null);
    }
}
