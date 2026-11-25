using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Storage;

namespace Themia.Conversations.Internal;

/// <summary>What a purge deleted; the same numbers go into the tombstone.</summary>
/// <param name="Messages">Messages deleted.</param>
/// <param name="Attachments">Attachment rows deleted.</param>
internal readonly record struct PurgeCounts(long Messages, long Attachments);

/// <summary>How the file step ended. <c>Held</c>: the hold port reported a hold after the claim, the room is now LegalHold and no file was touched. <c>LostClaim</c>: the claim is no longer ours.</summary>
internal enum PurgeFilesOutcome
{
    Deleted,
    Held,
    LostClaim,
    Failed,
}

/// <summary>How the row step ended. <c>Held</c>: a hold arrived after the claim, so the messages are kept. <c>LostClaim</c>: another finisher deleted the room or a hold took it first.</summary>
internal enum PurgeRowsOutcome
{
    Purged,
    Held,
    LostClaim,
}

internal readonly record struct PurgeRowsResult(PurgeRowsOutcome Outcome, PurgeCounts Counts);

/// <summary>
/// The one place a whole conversation is deleted, for the retention sweep and for an explicit room erase. Files go first, outside any transaction, while the caller holds the
/// <c>Purging</c> claim; then one transaction re-checks the claim, writes the tombstone and deletes every child table and the conversation row. Logging carries ids and counts only.
/// </summary>
internal sealed class ConversationPurger(IConversationDialect dialect, IConversationLifecycleSink sink, ILegalHoldPort? holdPort, IStorageProvider? storage, TimeProvider time, ILogger logger)
{
    private const int MaxAttempts = 3;
    private const int LegalHold = (int)ConversationRetentionState.LegalHold;
    private const int Purging = (int)ConversationRetentionState.Purging;

    /// <summary>
    /// Deletes every file of the conversation, staged uploads included, after asking the hold port once more: a hold that arrived since the caller's own check stops the purge before any file goes.
    /// <c>Failed</c> when any file could not be deleted or no storage is registered; the claim stays so a retry resumes.
    /// </summary>
    public async Task<PurgeFilesOutcome> DeleteFilesAsync(DbConnection connection, string tenantId, Guid conversationId, ConversationSubject subject, CancellationToken cancellationToken)
    {
        switch (await MoveToHoldIfHeldAsync(connection, tenantId, conversationId, subject, cancellationToken))
        {
            case HoldCheck.Held:
                return PurgeFilesOutcome.Held;
            case HoldCheck.LostClaim:
                return PurgeFilesOutcome.LostClaim;
        }

        var keys = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT storage_key FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "UNION SELECT storage_key FROM conversation_attachment_uploads WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", // staged files too: an upload in flight is reachable from here
            new { TenantId = tenantId, ConversationId = conversationId },
            cancellationToken: cancellationToken))).ToList();
        if (keys.Count == 0)
        {
            return PurgeFilesOutcome.Deleted;
        }

        if (storage is null)
        {
            logger.LogWarning("Conversation {ConversationId} has {Count} attachments but no IStorageProvider is registered; it stays claimed", conversationId, keys.Count);
            return PurgeFilesOutcome.Failed;
        }

        var ok = true;
        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(key, cancellationToken); // idempotent: an object already gone is fine
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                ok = false;
                logger.LogWarning("Conversation {ConversationId} lost an attachment delete ({ExceptionType}); it stays claimed and a retry resumes", conversationId, ex.GetType().Name);
            }
        }

        return ok ? PurgeFilesOutcome.Deleted : PurgeFilesOutcome.Failed;
    }

    /// <summary>
    /// Deletes the rows under the claim and writes the tombstone. The hold port is asked once more first, outside the transaction, because the file deletes can take a while and the messages
    /// are what a hold protects. <c>LostClaim</c> when the claim is no longer held (another finisher won, or a hold took the room).
    /// </summary>
    public async Task<PurgeRowsResult> DeleteRowsAsync(string tenantId, Guid conversationId, ConversationSubject subject, CancellationToken cancellationToken)
    {
        if (holdPort is not null)
        {
            await using var connection = dialect.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var check = await MoveToHoldIfHeldAsync(connection, tenantId, conversationId, subject, cancellationToken);
            if (check != HoldCheck.NotHeld)
            {
                if (check == HoldCheck.Held)
                {
                    var files = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                        "SELECT COUNT(*) FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;",
                        new { TenantId = tenantId, ConversationId = conversationId },
                        cancellationToken: cancellationToken));
                    logger.LogWarning("Conversation {ConversationId} was held after its files were deleted: the messages are kept, {Attachments} attachment rows no longer have a file", conversationId, files);
                }

                return new PurgeRowsResult(check == HoldCheck.Held ? PurgeRowsOutcome.Held : PurgeRowsOutcome.LostClaim, default);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DeleteRowsOnceAsync(tenantId, conversationId, subject.SubjectType, subject.SubjectId, cancellationToken);
            }
            catch (DbException ex) when (attempt < MaxAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Conversation {ConversationId} purge lost a lock race; retrying (attempt {Attempt})", conversationId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<PurgeRowsResult> DeleteRowsOnceAsync(string tenantId, Guid conversationId, string subjectType, string subjectId, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new { TenantId = tenantId, ConversationId = conversationId };

        // The claim is re-verified under the row lock: a conversation that is no longer Purging is not ours to delete.
        var locked = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET retention_state = 3 WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = 3;",
            scope,
            transaction,
            cancellationToken: cancellationToken));
        if (locked == 0)
        {
            return new PurgeRowsResult(PurgeRowsOutcome.LostClaim, default);
        }

        var messages = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));
        var attachments = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO conversation_tombstones (conversation_id, tenant_id, subject_type, subject_id, message_count, attachment_count, purged_at) VALUES (@ConversationId, @TenantId, @SubjectType, @SubjectId, @Messages, @Attachments, @Now);",
            new { TenantId = tenantId, ConversationId = conversationId, SubjectType = subjectType, SubjectId = subjectId, Messages = messages, Attachments = attachments, Now = time.GetUtcNow().UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));

        // Children first (their foreign keys point at participants and messages), then the conversation row, which frees the subject key.
        foreach (var table in new[] { "conversation_attachments", "conversation_attachment_uploads", "conversation_guest_sessions", "conversation_messages", "conversation_notify_cursors", "conversation_participants", "conversation_events" })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = 3;", scope, transaction, cancellationToken: cancellationToken));

        await sink.RaiseAsync(connection, transaction, new ConversationLifecycleEvent(Guid.NewGuid(), tenantId, conversationId, ConversationLifecycleKind.Purged, null, messages), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Conversation {ConversationId} purged: {Messages} messages, {Attachments} attachments", conversationId, messages, attachments);
        return new PurgeRowsResult(PurgeRowsOutcome.Purged, new PurgeCounts(messages, attachments));
    }

    private enum HoldCheck
    {
        NotHeld,
        Held,
        LostClaim,
    }

    /// <summary>Asks the port (never inside a transaction: it is an adopter call). A hold moves a still-claimed room to LegalHold; zero rows means the claim was already gone.</summary>
    private async Task<HoldCheck> MoveToHoldIfHeldAsync(DbConnection connection, string tenantId, Guid conversationId, ConversationSubject subject, CancellationToken cancellationToken)
    {
        if (holdPort is null || !await holdPort.IsHeldAsync(tenantId, subject, cancellationToken))
        {
            return HoldCheck.NotHeld;
        }

        var moved = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET retention_state = @LegalHold WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = @Purging;",
            new { TenantId = tenantId, ConversationId = conversationId, LegalHold, Purging },
            cancellationToken: cancellationToken));
        return moved == 1 ? HoldCheck.Held : HoldCheck.LostClaim;
    }
}
