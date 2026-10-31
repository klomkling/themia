using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// Staff oversight read. Order matters: policy and actor gate, then the audit port (so a missing port reveals nothing about any conversation), then one
/// transaction that loads the row, selects the page, records the read and commits; only after the commit does the caller get the page. Logging carries ids and counts only.
/// </summary>
internal sealed class ConversationOversightService(
    ConversationService store,
    IConversationReadAudit? audit,
    TimeProvider time,
    ILogger<ConversationOversightService> logger) : IConversationOversight
{
    /// <inheritdoc />
    public async Task<OversightPage> ReadAsync(ConversationActor actor, string tenantId, Guid conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (actor.Kind != ConversationActorKind.Member || actor.UserRef is null
            || !await store.AllowedAsync(actor, tenantId, conversationId, ConversationAction.OversightRead, cancellationToken))
        {
            return Empty(OversightReadOutcome.Refused);
        }

        if (audit is null)
        {
            logger.LogWarning("Oversight read refused in tenant {TenantId}: no IConversationReadAudit is registered", tenantId);
            return Empty(OversightReadOutcome.AuditPortMissing);
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var retentionState = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT retention_state FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;",
            new { TenantId = tenantId, ConversationId = conversationId }, transaction, cancellationToken: cancellationToken));
        if (retentionState is null || retentionState == (int)ConversationRetentionState.Purging)
        {
            return Empty(OversightReadOutcome.Refused);
        }

        var messages = await store.QueryPageAsync(connection, transaction, actor.Kind, tenantId, conversationId, afterSeq, limit, cancellationToken);
        try
        {
            await audit.RecordAsync(connection, transaction, new OversightRead(tenantId, conversationId, actor.Kind, actor.UserRef, messages.Count, time.GetUtcNow()), cancellationToken);
            // A port that swallowed its own SQL error leaves a PostgreSQL transaction aborted, and COMMIT of an aborted transaction rolls back
            // without an error. A trivial command on it raises 25P02 instead, so that case answers AuditFailed and not Listed with no audit row.
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
            logger.LogWarning("Oversight read of conversation {ConversationId} in tenant {TenantId} was not recorded ({ExceptionType}); nothing is returned", conversationId, tenantId, ex.GetType().Name);
            return Empty(OversightReadOutcome.AuditFailed);
        }

        return new OversightPage(OversightReadOutcome.Listed, messages);
    }

    private static OversightPage Empty(OversightReadOutcome outcome) => new(outcome, []);
}
