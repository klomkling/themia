using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Storage;

namespace Themia.Conversations.Internal;

/// <summary>
/// Retention over Dapper: expire, purge, hold. A purge claims the conversation (<c>Expired</c> to <c>Purging</c>), deletes its files outside any transaction, then deletes
/// the rows in one transaction that re-checks the claim. A hold port is asked before the claim, never inside a transaction. Logging carries ids and counts only.
/// </summary>
internal sealed class ConversationRetentionService(
    IConversationDialect dialect,
    ConversationOptions options,
    IConversationAccessPolicy policy,
    IConversationLifecycleSink sink,
    ILegalHoldPort? holdPort,
    IStorageProvider? storage,
    TimeProvider time,
    ILogger<ConversationRetentionService> logger) : IConversationRetention
{
    private const int MaxBatchSize = 1000;
    private const int MaxPurgeAttempts = 3;
    private const int Expired = (int)ConversationRetentionState.Expired;
    private const int Purging = (int)ConversationRetentionState.Purging;
    private const int LegalHold = (int)ConversationRetentionState.LegalHold;

    /// <inheritdoc />
    public async Task<SweepResult> SweepAsync(string tenantId, SweepOptions? sweep = null, CancellationToken cancellationToken = default)
    {
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        var batch = (sweep ?? new SweepOptions()).BatchSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch, MaxBatchSize);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var expired = await MarkExpiredAsync(connection, tenantId, cancellationToken);

        var portMissing = holdPort is null && !options.NoLegalHoldPort;
        if (portMissing)
        {
            logger.LogWarning("Conversation sweep for tenant {TenantId} marked {Expired} expired but purged nothing: no ILegalHoldPort is registered and NoLegalHoldPort is not set", tenantId, expired);
            return new SweepResult(SweepOutcome.HoldPortMissing, expired, 0, 0, 0);
        }

        var purged = 0;
        var held = 0;
        var failed = 0;
        Guid? after = null;

        // Page by id so a conversation that keeps failing is passed over within this call instead of heading every batch: only purges use up the batch size.
        while (purged < batch)
        {
            var page = await ClaimableAsync(connection, tenantId, after, batch, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var candidate in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after = candidate.Id;
                switch (await PurgeOneAsync(connection, tenantId, candidate, cancellationToken))
                {
                    case PurgeStep.Purged:
                        purged++;
                        break;
                    case PurgeStep.Held:
                        held++;
                        break;
                    case PurgeStep.Failed:
                        failed++;
                        break;
                }

                if (purged >= batch)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Conversation sweep for tenant {TenantId}: {Expired} expired, {Purged} purged, {Held} held, {Failed} failed", tenantId, expired, purged, held, failed);
        return new SweepResult(SweepOutcome.Completed, expired, purged, held, failed);
    }

    /// <summary>One set-based statement for every due conversation of the tenant: cheap, and it keeps a stuck conversation from holding the others back as Active.</summary>
    private async Task<int> MarkExpiredAsync(DbConnection connection, string tenantId, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET retention_state = 2 WHERE tenant_id = @TenantId AND state = 1 AND retention_state = 0 AND retention_until <= @Now;",
            new { TenantId = tenantId, Now = Now() },
            cancellationToken: cancellationToken));

    private async Task<List<Candidate>> ClaimableAsync(DbConnection connection, string tenantId, Guid? after, int pageSize, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<Candidate>(new CommandDefinition(
            "SELECT id AS Id, subject_type AS SubjectType, subject_id AS SubjectId, retention_state AS RetentionState FROM conversations " +
            "WHERE tenant_id = @TenantId AND retention_state IN (2, 3)" + (after is null ? string.Empty : " AND id > @After") + " ORDER BY id " + dialect.LimitClause + ";",
            new { TenantId = tenantId, After = after, Limit = pageSize },
            cancellationToken: cancellationToken))).ToList();

    private async Task<PurgeStep> PurgeOneAsync(DbConnection connection, string tenantId, Candidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            // The port is asked on every attempt, a resumed claim included: a hold placed after an earlier sweep claimed the conversation still stops the purge.
            if (holdPort is not null && await holdPort.IsHeldAsync(tenantId, new ConversationSubject(candidate.SubjectType, candidate.SubjectId), cancellationToken))
            {
                var held = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE conversations SET retention_state = 1 WHERE tenant_id = @TenantId AND id = @Id AND retention_state IN (2, 3);",
                    new { TenantId = tenantId, candidate.Id },
                    cancellationToken: cancellationToken));
                return held == 1 ? PurgeStep.Held : PurgeStep.Skipped;
            }

            if (candidate.RetentionState == Expired)
            {
                var claimed = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE conversations SET retention_state = 3 WHERE tenant_id = @TenantId AND id = @Id AND retention_state = 2;",
                    new { TenantId = tenantId, candidate.Id },
                    cancellationToken: cancellationToken));
                if (claimed == 0)
                {
                    return PurgeStep.Skipped; // reopened, held or purged since the read
                }
            }

            if (!await DeleteFilesAsync(connection, tenantId, candidate.Id, cancellationToken))
            {
                return PurgeStep.Failed;
            }

            return await DeleteRowsAsync(tenantId, candidate, cancellationToken) ? PurgeStep.Purged : PurgeStep.Skipped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The exception type only: a port or a provider message may quote a subject or a key.
            logger.LogWarning("Conversation {ConversationId} could not be purged this sweep ({ExceptionType}); it stays claimed and the next sweep resumes it", candidate.Id, ex.GetType().Name);
            return PurgeStep.Failed;
        }
    }

    private async Task<bool> DeleteFilesAsync(DbConnection connection, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var keys = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT storage_key FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;",
            new { TenantId = tenantId, ConversationId = conversationId },
            cancellationToken: cancellationToken))).ToList();
        if (keys.Count == 0)
        {
            return true;
        }

        if (storage is null)
        {
            logger.LogWarning("Conversation {ConversationId} has {Count} attachments but no IStorageProvider is registered; it stays claimed", conversationId, keys.Count);
            return false;
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
                logger.LogWarning("Conversation {ConversationId} lost an attachment delete ({ExceptionType}); it stays claimed and the next sweep retries", conversationId, ex.GetType().Name);
            }
        }

        return ok;
    }

    private async Task<bool> DeleteRowsAsync(string tenantId, Candidate candidate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DeleteRowsOnceAsync(tenantId, candidate, cancellationToken);
            }
            catch (DbException ex) when (attempt < MaxPurgeAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Conversation {ConversationId} purge lost a lock race; retrying (attempt {Attempt})", candidate.Id, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<bool> DeleteRowsOnceAsync(string tenantId, Candidate candidate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new { TenantId = tenantId, ConversationId = candidate.Id };

        // The claim is re-verified under the row lock: a conversation that is no longer Purging is not ours to delete.
        var locked = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET retention_state = 3 WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = 3;",
            scope,
            transaction,
            cancellationToken: cancellationToken));
        if (locked == 0)
        {
            return false;
        }

        var messages = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));
        var attachments = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO conversation_tombstones (conversation_id, tenant_id, subject_type, subject_id, message_count, attachment_count, purged_at) VALUES (@ConversationId, @TenantId, @SubjectType, @SubjectId, @Messages, @Attachments, @Now);",
            new { TenantId = tenantId, ConversationId = candidate.Id, candidate.SubjectType, candidate.SubjectId, Messages = messages, Attachments = attachments, Now = Now() },
            transaction,
            cancellationToken: cancellationToken));

        // Children first (their foreign keys point at participants and messages), then the conversation row, which frees the subject key.
        foreach (var table in new[] { "conversation_attachments", "conversation_guest_sessions", "conversation_messages", "conversation_participants", "conversation_events" })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = 3;", scope, transaction, cancellationToken: cancellationToken));

        await sink.RaiseAsync(connection, transaction, new ConversationLifecycleEvent(Guid.NewGuid(), tenantId, candidate.Id, ConversationLifecycleKind.Purged, null, messages), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Conversation {ConversationId} purged: {Messages} messages, {Attachments} attachments", candidate.Id, messages, attachments);
        return true;
    }

    /// <inheritdoc />
    public Task<HoldResult> PlaceHoldAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) =>
        ChangeHoldAsync(actor, tenantId, conversationId, place: true, cancellationToken);

    /// <inheritdoc />
    public Task<HoldResult> ReleaseHoldAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) =>
        ChangeHoldAsync(actor, tenantId, conversationId, place: false, cancellationToken);

    private async Task<HoldResult> ChangeHoldAsync(ConversationActor actor, string tenantId, Guid conversationId, bool place, CancellationToken cancellationToken)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));

        var action = place ? ConversationAction.Hold : ConversationAction.ReleaseHold;
        if (!await policy.CanAsync(actor, new ConversationTarget(tenantId, conversationId), action, cancellationToken))
        {
            return new HoldResult(HoldOutcome.Refused);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new { TenantId = tenantId, ConversationId = conversationId };

        // Active and Expired become LegalHold; LegalHold becomes Active. Purging matches neither, so a claimed conversation cannot be held.
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            place
                ? "UPDATE conversations SET retention_state = 1 WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state IN (0, 2);"
                : "UPDATE conversations SET retention_state = 0 WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state = 1;",
            scope,
            transaction,
            cancellationToken: cancellationToken));
        if (changed == 1)
        {
            var kind = place ? ConversationLifecycleKind.HoldPlaced : ConversationLifecycleKind.HoldReleased;
            await sink.RaiseAsync(connection, transaction, new ConversationLifecycleEvent(Guid.NewGuid(), tenantId, conversationId, kind, null, 0), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new HoldResult(place ? HoldOutcome.Placed : HoldOutcome.Released);
        }

        var state = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT retention_state FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;", scope, transaction, cancellationToken: cancellationToken));
        return new HoldResult(state switch
        {
            null => HoldOutcome.NotFound,
            Purging => HoldOutcome.PurgeInProgress,
            LegalHold => HoldOutcome.AlreadyHeld,
            _ => HoldOutcome.NotHeld,
        });
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

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

    private enum PurgeStep
    {
        Purged,
        Held,
        Skipped,
        Failed,
    }

    private sealed class Candidate
    {
        public Guid Id { get; set; }

        public string SubjectType { get; set; } = string.Empty;

        public string SubjectId { get; set; } = string.Empty;

        public int RetentionState { get; set; }
    }
}
