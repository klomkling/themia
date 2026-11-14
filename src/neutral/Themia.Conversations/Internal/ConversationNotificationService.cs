using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// The notification drain over Dapper (coord #0182). Seeds a cursor per active participant of a conversation with unsent events, claims due recipients with a lease in one short
/// transaction, calls the subscriber outside any transaction, then marks each recipient in its own short statement. Logging carries ids and counts only, never text or contact data.
/// </summary>
internal sealed class ConversationNotificationService(
    IConversationDialect dialect,
    ConversationOptions options,
    IConversationNotificationSubscriber? subscriber,
    TimeProvider time,
    ILogger<ConversationNotificationService> logger) : IConversationNotifications
{
    private const int MaxBatchSize = 1000;
    private const int SeedAttempts = 2;

    /// <inheritdoc />
    public async Task<DrainResult> DrainAsync(DrainOptions? drain = null, CancellationToken cancellationToken = default)
    {
        var batchSize = (drain ?? new DrainOptions()).BatchSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaxBatchSize);

        if (subscriber is null)
        {
            logger.LogWarning("Conversation notification drain did nothing: no IConversationNotificationSubscriber is registered");
            return new DrainResult(DrainOutcome.SubscriberMissing, 0, 0, 0, 0);
        }

        var now = time.GetUtcNow();
        var owner = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await SeedCursorsAsync(connection, now, cancellationToken);
        var claimed = await ClaimAsync(connection, owner, now, batchSize, cancellationToken);

        var delivered = 0;
        var skipped = 0;
        var failed = 0;
        var leaseLost = 0;
        foreach (var key in claimed)
        {
            var outcome = await NotifyOneAsync(connection, subscriber, key, owner, cancellationToken);
            switch (outcome)
            {
                case RecipientOutcome.Delivered: delivered++; break;
                case RecipientOutcome.Skipped: skipped++; break;
                case RecipientOutcome.Failed: failed++; break;
                case RecipientOutcome.LeaseLost: leaseLost++; break;
                default: throw new InvalidOperationException($"Unknown recipient outcome {outcome}.");
            }
        }

        if (time.GetUtcNow() - now > options.NotifyLeaseDuration)
        {
            logger.LogWarning("Conversation notification drain ran longer than the lease of {LeaseSeconds} s; a recipient may have been notified twice", options.NotifyLeaseDuration.TotalSeconds);
        }

        await new ConversationEventSweeper(dialect, logger).SweepAsync(connection, time.GetUtcNow(), now - options.NotifyDebounce, batchSize, cancellationToken);
        return new DrainResult(DrainOutcome.Completed, delivered, skipped, failed, leaseLost);
    }

    /// <summary>
    /// Insert-if-absent, as its own committed statement before the claim, so the claim never inserts under its own lock. A unique-key conflict means another drain seeded the
    /// same rows first: the whole statement rolled back, so try once more, then leave the rest to the next drain.
    /// </summary>
    private async Task SeedCursorsAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO conversation_notify_cursors (tenant_id, conversation_id, participant_id, notified_seq, created_at, updated_at) " +
            "SELECT p.tenant_id, p.conversation_id, p.id, 0, @Now, @Now FROM conversation_participants p " +
            "WHERE p.left_at IS NULL " +
            "AND EXISTS (SELECT 1 FROM conversation_events e WHERE e.tenant_id = p.tenant_id AND e.conversation_id = p.conversation_id AND e.kind = 0 AND e.delivered_at IS NULL) " +
            "AND NOT EXISTS (SELECT 1 FROM conversation_notify_cursors c WHERE c.tenant_id = p.tenant_id AND c.conversation_id = p.conversation_id AND c.participant_id = p.id);";

        for (var attempt = 1; attempt <= SeedAttempts; attempt++)
        {
            try
            {
                // ReadCommitted, not the connection default: MySQL autocommit runs INSERT ... SELECT at REPEATABLE READ and would lock the rows it reads (participants, events).
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(sql, new { Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch (DbException exception) when (dialect.IsDuplicateKey(exception) || dialect.IsDeadlock(exception))
            {
                logger.LogDebug("Conversation notification cursor seed met a concurrent drain, a duplicate key or a deadlock (attempt {Attempt})", attempt);
            }
        }
    }

    /// <summary>The claim query: the due recipients, locked on the cursor row only. Internal so a test can hold the real statement open on a second connection.</summary>
    internal string ClaimSelectSql =>
            "SELECT n.tenant_id AS TenantId, n.conversation_id AS ConversationId, n.participant_id AS ParticipantId " +
            $"FROM conversation_notify_cursors n {dialect.CursorClaimTableHint} " +
            "JOIN conversation_participants p ON p.tenant_id = n.tenant_id AND p.conversation_id = n.conversation_id AND p.id = n.participant_id " +
            "WHERE p.left_at IS NULL AND (n.lease_owner IS NULL OR n.lease_expires_at <= @Now) " +
            "AND EXISTS (SELECT 1 FROM conversation_messages m WHERE m.tenant_id = n.tenant_id AND m.conversation_id = n.conversation_id " +
            "AND m.participant_id <> n.participant_id AND m.redacted_at IS NULL AND m.created_at <= @Cutoff " +
            "AND m.seq > CASE WHEN n.notified_seq > p.last_read_seq THEN n.notified_seq ELSE p.last_read_seq END) " +
            $"ORDER BY n.updated_at {dialect.LimitClause} {dialect.CursorClaimRowLockClause};";

    private async Task<List<CursorKey>> ClaimAsync(DbConnection connection, Guid owner, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var rows = (await connection.QueryAsync<CursorKey>(new CommandDefinition(
            ClaimSelectSql,
            new { Now = now.UtcDateTime, Cutoff = (now - options.NotifyDebounce).UtcDateTime, Limit = batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        var lease = (now + options.NotifyLeaseDuration).UtcDateTime;
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE conversation_notify_cursors SET lease_owner = @Owner, lease_expires_at = @Lease, updated_at = @Now " +
                "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId;",
                new { Owner = owner, Lease = lease, Now = now.UtcDateTime, row.TenantId, row.ConversationId, row.ParticipantId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task<RecipientOutcome> NotifyOneAsync(DbConnection connection, IConversationNotificationSubscriber target, CursorKey key, Guid owner, CancellationToken cancellationToken)
    {
        var batch = await BuildBatchAsync(connection, key, cancellationToken);
        if (batch.ReadSeq is null)
        {
            await ReleaseAsync(connection, key, owner, cancellationToken);
            return RecipientOutcome.Skipped;
        }

        if (batch.UnreadCount == 0)
        {
            var marked = await MarkAsync(connection, key, owner, batch.ReadSeq.Value, cancellationToken);
            return marked ? RecipientOutcome.Skipped : LostLease(key);
        }

        var notification = new ConversationNotification(BatchIdFor(key, batch.LastSeq), key.TenantId, key.ConversationId, key.ParticipantId, batch.UnreadCount, batch.LastSeq);
        try
        {
            await target.NotifyAsync(notification, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type name and ids only: the subscriber is app code talking to a channel, and its message can carry an address, a number or text.
            logger.LogWarning("Conversation notification failed with {ExceptionType} for participant {ParticipantId} in conversation {ConversationId}; it is retried on the next drain", exception.GetType().Name, key.ParticipantId, key.ConversationId);
            await ReleaseAsync(connection, key, owner, cancellationToken);
            return RecipientOutcome.Failed;
        }

        return await MarkAsync(connection, key, owner, batch.LastSeq, cancellationToken) ? RecipientOutcome.Delivered : LostLease(key);
    }

    private RecipientOutcome LostLease(CursorKey key)
    {
        logger.LogWarning("Conversation notification lease lost for participant {ParticipantId} in conversation {ConversationId}: another drain took the recipient over", key.ParticipantId, key.ConversationId);
        return RecipientOutcome.LeaseLost;
    }

    private static async Task<Batch> BuildBatchAsync(DbConnection connection, CursorKey key, CancellationToken cancellationToken)
    {
        var parameters = new { key.TenantId, key.ConversationId, key.ParticipantId };
        var read = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT CASE WHEN n.notified_seq > p.last_read_seq THEN n.notified_seq ELSE p.last_read_seq END " +
            "FROM conversation_notify_cursors n JOIN conversation_participants p ON p.tenant_id = n.tenant_id AND p.conversation_id = n.conversation_id AND p.id = n.participant_id " +
            "WHERE n.tenant_id = @TenantId AND n.conversation_id = @ConversationId AND n.participant_id = @ParticipantId AND p.left_at IS NULL;",
            parameters,
            cancellationToken: cancellationToken));
        if (read is null)
        {
            return new Batch(0, 0, null); // The recipient left (or the cursor is gone) since the claim: nothing to send.
        }

        var counted = await connection.QuerySingleAsync<Counted>(new CommandDefinition(
            "SELECT COUNT(*) AS UnreadCount, COALESCE(MAX(seq), 0) AS LastSeq FROM conversation_messages " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id <> @ParticipantId AND redacted_at IS NULL AND seq > @After;",
            new { key.TenantId, key.ConversationId, key.ParticipantId, After = read.Value },
            cancellationToken: cancellationToken));
        return new Batch((int)counted.UnreadCount, counted.LastSeq, read);
    }

    private async Task<bool> MarkAsync(DbConnection connection, CursorKey key, Guid owner, long seq, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_notify_cursors SET notified_seq = CASE WHEN notified_seq < @Seq THEN @Seq ELSE notified_seq END, lease_owner = NULL, lease_expires_at = NULL, updated_at = @Now " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId AND lease_owner = @Owner;",
            new { Seq = seq, Now = time.GetUtcNow().UtcDateTime, Owner = owner, key.TenantId, key.ConversationId, key.ParticipantId },
            cancellationToken: cancellationToken)) > 0;

    private async Task ReleaseAsync(DbConnection connection, CursorKey key, Guid owner, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_notify_cursors SET lease_owner = NULL, lease_expires_at = NULL, updated_at = @Now " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND participant_id = @ParticipantId AND lease_owner = @Owner;",
            new { Now = time.GetUtcNow().UtcDateTime, Owner = owner, key.TenantId, key.ConversationId, key.ParticipantId },
            cancellationToken: cancellationToken));

    /// <summary>Deterministic from (conversation, participant, last seq): a re-push with no new message carries the same id, a superseding batch a higher seq and a new id.</summary>
    internal static Guid BatchIdFor(CursorKey key, long lastSeq)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key.TenantId}|{key.ConversationId:N}|{key.ParticipantId:N}|{lastSeq}"));
        return new Guid(hash.AsSpan(0, 16));
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

    internal sealed class CursorKey
    {
        public string TenantId { get; init; } = string.Empty;

        public Guid ConversationId { get; init; }

        public Guid ParticipantId { get; init; }
    }

    private sealed class Counted
    {
        public long UnreadCount { get; init; }

        public long LastSeq { get; init; }
    }

    private readonly record struct Batch(int UnreadCount, long LastSeq, long? ReadSeq);

    private enum RecipientOutcome
    {
        Delivered,
        Skipped,
        Failed,
        LeaseLost,
    }
}
