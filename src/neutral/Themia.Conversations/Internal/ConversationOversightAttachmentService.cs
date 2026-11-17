using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// Staff oversight open of a file. The order is that of <see cref="ConversationOversightService"/>: policy and actor gate, the audit port, then one transaction that
/// checks the room, finds the attachment, records the open and commits; the URL is signed only after the commit, so no URL exists for an open that was not recorded.
/// Logging carries ids only, never a file name or a URL.
/// </summary>
internal sealed class ConversationOversightAttachmentService(
    ConversationService store,
    IConversationReadAudit? audit,
    TimeProvider time,
    ILogger<ConversationOversightAttachmentService> logger) : IConversationOversightAttachments
{
    /// <inheritdoc />
    public async Task<OversightOpenResult> OpenAsync(ConversationActor actor, string tenantId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));

        if (actor.Kind != ConversationActorKind.Member || actor.UserRef is null
            || !await store.AllowedAsync(actor, tenantId, conversationId, ConversationAction.OversightRead, cancellationToken))
        {
            return Empty(OversightOpenOutcome.Refused);
        }

        if (audit is null)
        {
            logger.LogWarning("Oversight file open refused in tenant {TenantId}: no IConversationReadAudit is registered", tenantId);
            return Empty(OversightOpenOutcome.AuditPortMissing);
        }

        ConversationService.AttachmentRow row;
        await using (var connection = await store.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var retentionState = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                "SELECT retention_state FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;",
                new { TenantId = tenantId, ConversationId = conversationId }, transaction, cancellationToken: cancellationToken));
            if (retentionState is null || retentionState == (int)ConversationRetentionState.Purging)
            {
                return Empty(OversightOpenOutcome.Refused);
            }

            if (await store.FindAttachmentAsync(connection, transaction, tenantId, conversationId, attachmentId, cancellationToken) is not { } found)
            {
                return Empty(OversightOpenOutcome.Refused);
            }

            row = found;
            try
            {
                var entry = new OversightRead(tenantId, conversationId, actor.Kind, actor.UserRef, 1, time.GetUtcNow()) { AttachmentId = attachmentId };
                await audit.RecordAsync(connection, transaction, entry, cancellationToken);
                // A port that swallowed its own SQL error leaves a PostgreSQL transaction aborted; a trivial command raises 25P02 so that case answers AuditFailed.
                await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", transaction: transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The exception type only: an audit port's message may quote what it tried to write.
                logger.LogWarning("Oversight open of an attachment in conversation {ConversationId} in tenant {TenantId} was not recorded ({ExceptionType}); no URL is issued", conversationId, tenantId, ex.GetType().Name);
                return Empty(OversightOpenOutcome.AuditFailed);
            }
        }

        var opened = await store.IssueDownloadAsync(row, actor.Kind, cancellationToken);
        return new OversightOpenResult(OversightOpenOutcome.Opened, opened.Url, opened.Attachment);
    }

    private static OversightOpenResult Empty(OversightOpenOutcome outcome) => new(outcome, null, null);
}
