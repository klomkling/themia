using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

internal sealed partial class ConversationService
{
    private const int MaxPostAttempts = 3;

    /// <inheritdoc />
    public async Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(message);
        ConversationInput.ClientMessageId(message.ClientMessageId);

        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Post, cancellationToken))
        {
            return new PostResult(PostOutcome.Refused, null, null);
        }

        if (string.IsNullOrWhiteSpace(message.Body) || message.Body.Length > options.MaxBodyLength)
        {
            return new PostResult(PostOutcome.InvalidMessage, null, null);
        }

        // A deadlock or lock timeout rolls the whole transaction back; the retry runs the whole transaction again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(actor, tenantId, conversationId, message, cancellationToken);
            }
            catch (DbException ex) when (attempt < MaxPostAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Conversation {ConversationId} post lost a lock race; retrying (attempt {Attempt})", conversationId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<PostResult> PostOnceAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Order: participant, idempotency read, counter, inserts. The counter row lock is taken last-but-one and held to commit, so posts to one conversation serialise there.
        if (await ResolveParticipantAsync(connection, transaction, actor, tenantId, conversationId, cancellationToken) is not { } participantId)
        {
            return new PostResult(PostOutcome.Refused, null, null);
        }

        if (await FindMessageAsync(connection, transaction, tenantId, conversationId, participantId, message.ClientMessageId, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (await dialect.IncrementLastSeqAsync(connection, transaction, tenantId, conversationId, cancellationToken) is not { } seq)
        {
            return new PostResult(PostOutcome.Closed, null, null);
        }

        var messageId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO conversation_messages (id, tenant_id, conversation_id, seq, participant_id, kind, body, client_message_id, created_at) VALUES (@Id, @TenantId, @ConversationId, @Seq, @ParticipantId, @Kind, @Body, @ClientMessageId, @Now);",
                new { Id = messageId, TenantId = tenantId, ConversationId = conversationId, Seq = seq, ParticipantId = participantId, Kind = (short)MessageKind.Text, message.Body, message.ClientMessageId, Now = Now() },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (DbException ex) when (dialect.IsDuplicateKey(ex))
        {
            // A concurrent post of the same client message id won the unique index. Roll back (the counter bump goes with it, so the sequence stays dense; a
            // PostgreSQL transaction is aborted anyway) and answer with the stored message.
            await transaction.RollbackAsync(CancellationToken.None);
            await using var reader = await OpenConnectionAsync(cancellationToken);
            return await FindMessageAsync(reader, null, tenantId, conversationId, participantId, message.ClientMessageId, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate client message id was reported but no stored message was found.");
        }

        var recipients = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM conversation_participants WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND left_at IS NULL AND id <> @ParticipantId;",
            new { TenantId = tenantId, ConversationId = conversationId, ParticipantId = participantId },
            transaction,
            cancellationToken: cancellationToken))).ToList();
        await sink.RaiseAsync(connection, transaction, new ConversationEvent(Guid.NewGuid(), tenantId, conversationId, ConversationEventKind.MessagePosted, messageId, seq, recipients), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Conversation {ConversationId} message {MessageId} stored as seq {Seq}", conversationId, messageId, seq);
        return new PostResult(PostOutcome.Posted, messageId, seq);
    }

    private static async Task<PostResult?> FindMessageAsync(DbConnection connection, DbTransaction? transaction, string tenantId, Guid conversationId, Guid participantId, string clientMessageId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StoredMessage>(new CommandDefinition(
            "SELECT id AS Id, seq AS Seq FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId AND client_message_id = @ClientMessageId;",
            new { TenantId = tenantId, ConversationId = conversationId, ParticipantId = participantId, ClientMessageId = clientMessageId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : new PostResult(PostOutcome.Duplicate, row.Id, row.Seq);
    }

    private sealed class StoredMessage
    {
        public Guid Id { get; set; }

        public long Seq { get; set; }
    }
}
