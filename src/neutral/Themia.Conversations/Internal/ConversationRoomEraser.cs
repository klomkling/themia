using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// Deletes one whole conversation on request, through <see cref="ConversationPurger"/>, the code the retention sweep uses. Order: policy, find the room (never creating one), ask the
/// legal-hold port, claim <c>Purging</c>, refuse while an upload is in flight, delete files, delete rows. A failure after the claim leaves the room claimed with its rows, so a retry
/// resumes. Logging carries ids and counts only.
/// </summary>
internal sealed class ConversationRoomEraser(
    IConversationDialect dialect,
    ConversationOptions options,
    IConversationAccessPolicy policy,
    ILegalHoldPort? holdPort,
    ConversationPurger purger,
    TimeProvider time,
    ILogger logger)
{
    private const int Active = (int)ConversationRetentionState.Active;
    private const int Expired = (int)ConversationRetentionState.Expired;
    private const int LegalHold = (int)ConversationRetentionState.LegalHold;

    public async Task<ConversationPurgeResult> EraseAsync(ConversationActor actor, string tenantId, Guid? conversationId, ConversationSubject? subject, CancellationToken cancellationToken)
    {
        if (!await policy.CanAsync(actor, new ConversationTarget(tenantId, conversationId, subject), ConversationAction.PurgeConversation, cancellationToken))
        {
            return Result(ConversationPurgeOutcome.Refused);
        }

        string? logId = conversationId?.ToString();
        try
        {
            await using var connection = dialect.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            if (await FindAsync(connection, tenantId, conversationId, subject, cancellationToken) is not { } room)
            {
                return await NotFoundAsync(connection, tenantId, conversationId, subject, cancellationToken);
            }

            logId = room.Id.ToString();
            return await EraseRoomAsync(connection, tenantId, room, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Conversation {ConversationId} could not be erased ({ExceptionType}); ask again", logId, ex.GetType().Name);
            return Result(ConversationPurgeOutcome.Failed);
        }
    }

    private async Task<ConversationPurgeResult> EraseRoomAsync(DbConnection connection, string tenantId, Room room, CancellationToken cancellationToken)
    {
        // The port is the truth for holds and the mirrored state can lag, so it is asked on every call, a resumed claim included.
        if (holdPort is null && !options.NoLegalHoldPort)
        {
            return Result(ConversationPurgeOutcome.Deferred); // fail closed: no way to know about a hold
        }

        if (room.RetentionState == LegalHold || (holdPort is not null && await holdPort.IsHeldAsync(tenantId, new ConversationSubject(room.SubjectType, room.SubjectId), cancellationToken)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE conversations SET retention_state = 1 WHERE tenant_id = @TenantId AND id = @Id AND retention_state IN (0, 2, 3);",
                new { TenantId = tenantId, room.Id },
                cancellationToken: cancellationToken));
            return Result(ConversationPurgeOutcome.Deferred);
        }

        // Claim before any file is touched, so a hold placed after this point finds a Purging room and cannot land on a half-deleted one.
        var claimedHere = false;
        if (room.RetentionState is Active or Expired)
        {
            claimedHere = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE conversations SET retention_state = 3 WHERE tenant_id = @TenantId AND id = @Id AND retention_state IN (0, 2);",
                new { TenantId = tenantId, room.Id },
                cancellationToken: cancellationToken)) == 1;
            if (!claimedHere)
            {
                return await ResolveLostClaimAsync(connection, tenantId, room, cancellationToken);
            }
        }

        // Staging rows are written under the room lock and only before the claim, so after it this count is final. A young one may still be about to PutAsync an object.
        if (await UploadInFlightAsync(connection, tenantId, room.Id, cancellationToken))
        {
            if (claimedHere)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE conversations SET retention_state = @Prior WHERE tenant_id = @TenantId AND id = @Id AND retention_state = 3;",
                    new { TenantId = tenantId, room.Id, Prior = room.RetentionState },
                    cancellationToken: cancellationToken)); // nothing was deleted yet, so giving the room back is safe
            }

            return Result(ConversationPurgeOutcome.Deferred);
        }

        var subject = new ConversationSubject(room.SubjectType, room.SubjectId);
        switch (await purger.DeleteFilesAsync(connection, tenantId, room.Id, subject, cancellationToken))
        {
            case PurgeFilesOutcome.Failed:
                return Result(ConversationPurgeOutcome.Failed);
            case PurgeFilesOutcome.Held or PurgeFilesOutcome.LostClaim:
                return await ResolveLostClaimAsync(connection, tenantId, room, cancellationToken); // gone is NotFound, present (held) is Deferred
        }

        var rows = await purger.DeleteRowsAsync(tenantId, room.Id, subject, cancellationToken);
        return rows.Outcome == PurgeRowsOutcome.Purged
            ? new ConversationPurgeResult(ConversationPurgeOutcome.Purged, Clamp(rows.Counts.Messages), Clamp(rows.Counts.Attachments))
            : await ResolveLostClaimAsync(connection, tenantId, room, cancellationToken);
    }

    /// <summary>The claim did not hold: another finisher deleted the room (NotFound, with the tombstone's counts) or a hold took it (Deferred).</summary>
    private async Task<ConversationPurgeResult> ResolveLostClaimAsync(DbConnection connection, string tenantId, Room room, CancellationToken cancellationToken)
    {
        var now = await FindAsync(connection, tenantId, room.Id, null, cancellationToken);
        return now is null
            ? await NotFoundAsync(connection, tenantId, room.Id, null, cancellationToken)
            : Result(ConversationPurgeOutcome.Deferred);
    }

    private async Task<bool> UploadInFlightAsync(DbConnection connection, string tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversation_attachment_uploads WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND created_at > @Cutoff;",
            new { TenantId = tenantId, ConversationId = conversationId, Cutoff = (time.GetUtcNow() - options.AttachmentUploadGrace).UtcDateTime },
            cancellationToken: cancellationToken)) > 0;

    private static async Task<Room?> FindAsync(DbConnection connection, string tenantId, Guid? conversationId, ConversationSubject? subject, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<Room>(new CommandDefinition(
            "SELECT id AS Id, subject_type AS SubjectType, subject_id AS SubjectId, retention_state AS RetentionState FROM conversations WHERE tenant_id = @TenantId AND " +
            (conversationId is null ? "subject_type = @SubjectType AND subject_id = @SubjectId;" : "id = @Id;"),
            new { TenantId = tenantId, Id = conversationId, subject?.SubjectType, subject?.SubjectId },
            cancellationToken: cancellationToken));

    /// <summary>The room is gone: answer with the counts its tombstone holds, so a retry reads what the first call deleted, not zero.</summary>
    private async Task<ConversationPurgeResult> NotFoundAsync(DbConnection connection, string tenantId, Guid? conversationId, ConversationSubject? subject, CancellationToken cancellationToken)
    {
        var tombstone = await connection.QueryFirstOrDefaultAsync<Tombstone>(new CommandDefinition(
            "SELECT message_count AS Messages, attachment_count AS Attachments FROM conversation_tombstones WHERE tenant_id = @TenantId AND " +
            (conversationId is null ? "subject_type = @SubjectType AND subject_id = @SubjectId" : "conversation_id = @Id") + " ORDER BY purged_at DESC " + dialect.LimitClause + ";",
            new { TenantId = tenantId, Id = conversationId, subject?.SubjectType, subject?.SubjectId, Limit = 1 },
            cancellationToken: cancellationToken));
        return new ConversationPurgeResult(ConversationPurgeOutcome.NotFound, Clamp(tombstone?.Messages ?? 0), Clamp(tombstone?.Attachments ?? 0));
    }

    private static ConversationPurgeResult Result(ConversationPurgeOutcome outcome) => new(outcome, 0, 0);

    private static int Clamp(long count) => (int)Math.Min(count, int.MaxValue);

    private sealed class Room
    {
        public Guid Id { get; set; }

        public string SubjectType { get; set; } = string.Empty;

        public string SubjectId { get; set; } = string.Empty;

        public int RetentionState { get; set; }
    }

    private sealed class Tombstone
    {
        public long Messages { get; set; }

        public long Attachments { get; set; }
    }
}
