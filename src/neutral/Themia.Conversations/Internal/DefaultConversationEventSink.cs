using System.Data.Common;
using System.Text.Json;
using Dapper;

namespace Themia.Conversations.Internal;

/// <summary>The default <see cref="IConversationEventSink"/>: one row in <c>conversation_events</c> in the post transaction. The payload holds ids and the sequence number, never the text.</summary>
internal sealed class DefaultConversationEventSink(TimeProvider time) : IConversationEventSink
{
    public Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationEvent);

        var payload = JsonSerializer.Serialize(new
        {
            messageId = conversationEvent.MessageId,
            seq = conversationEvent.Seq,
            recipients = conversationEvent.RecipientParticipantIds,
        });

        return connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO conversation_events (id, tenant_id, conversation_id, kind, payload, created_at) VALUES (@Id, @TenantId, @ConversationId, @Kind, @Payload, @Now);",
            new
            {
                conversationEvent.Id,
                conversationEvent.TenantId,
                conversationEvent.ConversationId,
                Kind = (short)conversationEvent.Kind,
                Payload = payload,
                Now = time.GetUtcNow().UtcDateTime,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
