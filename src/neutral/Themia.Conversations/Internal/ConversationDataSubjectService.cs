using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Storage;

namespace Themia.Conversations.Internal;

/// <summary>
/// Per-person erasure over Dapper. For each conversation the person is in: read the file keys without a lock, delete the files outside any transaction, then in one
/// transaction lock the conversation, re-check that it is not held or being purged, redact the person's messages, delete their attachment rows and guest sessions, and
/// anonymise the participant row. Other participants' rows and the message sequence keep their shape. Logging carries ids and counts only.
/// </summary>
internal sealed class ConversationDataSubjectService(
    IConversationDialect dialect,
    IConversationAccessPolicy policy,
    IConversationLifecycleSink sink,
    ConversationGuestOptions? guestOptions,
    IStorageProvider? storage,
    TimeProvider time,
    ILogger<ConversationDataSubjectService> logger) : IConversationDataSubject
{
    internal const string ErasedDisplayName = "Erased participant";
    private const int MaxAttempts = 3;

    /// <inheritdoc />
    public async Task<ErasureResult> ErasePersonAsync(ConversationActor actor, string tenantId, PersonRef person, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(person);

        if (!await policy.CanAsync(actor, new ConversationTarget(tenantId, null), ConversationAction.Erase, cancellationToken))
        {
            return Empty(ErasureOutcome.Refused);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await FindParticipantsAsync(connection, tenantId, person, cancellationToken) is not { } found)
        {
            return Empty(ErasureOutcome.Invalid);
        }

        var erased = 0;
        var redacted = 0;
        var attachments = 0;
        var deferred = new List<Guid>();
        var failed = new List<Guid>();
        foreach (var (participantId, conversationId) in found)
        {
            var step = await EraseOneAsync(connection, tenantId, conversationId, participantId, cancellationToken);
            switch (step.Result)
            {
                case Step.Erased:
                    erased++;
                    redacted += step.Redacted;
                    attachments += step.Attachments;
                    break;
                case Step.Deferred:
                    deferred.Add(conversationId);
                    break;
                case Step.Failed:
                    failed.Add(conversationId);
                    break;
            }
        }

        var outcome = deferred.Count + failed.Count == 0 ? ErasureOutcome.Erased : erased > 0 ? ErasureOutcome.PartiallyErased : ErasureOutcome.Deferred;
        logger.LogInformation("Erased a person in {Erased} conversations ({Redacted} messages, {Attachments} attachments); {Deferred} deferred, {Failed} failed", erased, redacted, attachments, deferred.Count, failed.Count);
        return new ErasureResult(outcome, erased, redacted, attachments, deferred, failed);
    }

    private static ErasureResult Empty(ErasureOutcome outcome) => new(outcome, 0, 0, 0, [], []);

    /// <summary>The participant rows of this person in this tenant, or <see langword="null"/> when the reference cannot be resolved.</summary>
    private async Task<List<(Guid ParticipantId, Guid ConversationId)>?> FindParticipantsAsync(DbConnection connection, string tenantId, PersonRef person, CancellationToken cancellationToken)
    {
        if (person.Kind is ConversationActorKind.Member or ConversationActorKind.Partner)
        {
            if (string.IsNullOrWhiteSpace(person.UserRef) || person.UserRef.Length > ConversationInput.MaxUserRefLength || char.IsWhiteSpace(person.UserRef[0]) || char.IsWhiteSpace(person.UserRef[^1]))
            {
                return null;
            }

            var stored = ConversationInput.StoredUserRef(new ConversationActor(person.Kind, person.UserRef));
            return await ListAsync(connection, "kind = 0 AND user_ref = @Key", new { TenantId = tenantId, Key = stored }, cancellationToken);
        }

        if (person.Kind != ConversationActorKind.Guest || guestOptions is null || !GuestContactHasher.TryNormalise(person.Contact, out var input))
        {
            return null;
        }

        // A guest hashed under an older pepper is still found: every configured pepper is tried.
        var rows = new List<(Guid, Guid)>();
        foreach (var pepper in guestOptions.Peppers.Values)
        {
            rows.AddRange(await ListAsync(connection, "kind = 1 AND contact_hash = @Key", new { TenantId = tenantId, Key = GuestContactHasher.Hash(pepper, input) }, cancellationToken));
        }

        return rows.Distinct().ToList();
    }

    private static async Task<List<(Guid, Guid)>> ListAsync(DbConnection connection, string predicate, object parameters, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<(Guid, Guid)>(new CommandDefinition(
            $"SELECT id, conversation_id FROM conversation_participants WHERE tenant_id = @TenantId AND {predicate};",
            parameters,
            cancellationToken: cancellationToken))).ToList();

    private async Task<StepResult> EraseOneAsync(DbConnection connection, string tenantId, Guid conversationId, Guid participantId, CancellationToken cancellationToken)
    {
        try
        {
            // Read without a lock: a hold or a purge claim means the file deletes below must not start.
            var state = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT retention_state FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId;",
                new { TenantId = tenantId, ConversationId = conversationId },
                cancellationToken: cancellationToken));
            if (state is not (int)ConversationRetentionState.Active and not (int)ConversationRetentionState.Expired)
            {
                return new StepResult(Step.Deferred, 0, 0);
            }

            if (!await DeleteFilesAsync(connection, tenantId, conversationId, participantId, cancellationToken))
            {
                return new StepResult(Step.Failed, 0, 0);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await EraseRowsAsync(tenantId, conversationId, participantId, cancellationToken);
                }
                catch (DbException ex) when (attempt < MaxAttempts && dialect.IsDeadlock(ex))
                {
                    logger.LogWarning("Conversation {ConversationId} erasure lost a lock race; retrying (attempt {Attempt})", conversationId, attempt + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Conversation {ConversationId} could not be erased ({ExceptionType}); its rows are untouched", conversationId, ex.GetType().Name);
            return new StepResult(Step.Failed, 0, 0);
        }
    }

    /// <summary>The attachments the person owns or sent: the person's own file, plus anything attached to a message they posted.</summary>
    private const string PersonAttachments =
        "tenant_id = @TenantId AND conversation_id = @ConversationId AND (owner_participant_id = @ParticipantId OR message_id IN " +
        "(SELECT id FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId))";

    private async Task<bool> DeleteFilesAsync(DbConnection connection, string tenantId, Guid conversationId, Guid participantId, CancellationToken cancellationToken)
    {
        var keys = (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT storage_key FROM conversation_attachments WHERE {PersonAttachments};",
            new { TenantId = tenantId, ConversationId = conversationId, ParticipantId = participantId },
            cancellationToken: cancellationToken))).ToList();
        if (keys.Count == 0)
        {
            return true;
        }

        if (storage is null)
        {
            logger.LogWarning("Conversation {ConversationId} has {Count} attachments of the person but no IStorageProvider is registered", conversationId, keys.Count);
            return false;
        }

        foreach (var key in keys)
        {
            await storage.DeleteAsync(key, cancellationToken); // idempotent: an object already gone is fine; a failure leaves every row untouched
        }

        return true;
    }

    private async Task<StepResult> EraseRowsAsync(string tenantId, Guid conversationId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new { TenantId = tenantId, ConversationId = conversationId, ParticipantId = participantId, Now = time.GetUtcNow().UtcDateTime, Label = ErasedDisplayName };

        // Conversation row first, as a post does; a hold or a purge claim placed since the read wins (the files are gone already: the accepted, documented race).
        var locked = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversations SET retention_state = retention_state WHERE tenant_id = @TenantId AND id = @ConversationId AND retention_state IN (0, 2);",
            scope, transaction, cancellationToken: cancellationToken));
        if (locked == 0)
        {
            return new StepResult(Step.Deferred, 0, 0);
        }

        var attachments = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM conversation_attachments WHERE {PersonAttachments};", scope, transaction, cancellationToken: cancellationToken));
        var redacted = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_messages SET body = NULL, redacted_at = @Now WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId AND redacted_at IS NULL;",
            scope, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM conversation_guest_sessions WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId;",
            scope, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_participants SET display_name = @Label, user_ref = NULL, contact_hash = NULL, pepper_key_id = NULL, role_label = NULL, left_at = COALESCE(left_at, @Now) " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND id = @ParticipantId;",
            scope, transaction, cancellationToken: cancellationToken));

        await sink.RaiseAsync(connection, transaction, new ConversationLifecycleEvent(Guid.NewGuid(), tenantId, conversationId, ConversationLifecycleKind.ParticipantErased, participantId, redacted), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StepResult(Step.Erased, redacted, attachments);
    }

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

    private enum Step
    {
        Erased,
        Deferred,
        Failed,
    }

    private readonly record struct StepResult(Step Result, int Redacted, int Attachments);
}
