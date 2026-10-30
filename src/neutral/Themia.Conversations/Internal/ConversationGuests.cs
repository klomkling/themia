using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Challenges;

namespace Themia.Conversations.Internal;

/// <summary>
/// Guest invites and the exchange of a link token for a session. Scoped, because <see cref="IChallengeService"/> is. Logging carries ids and outcomes only: never a token, a
/// session key, a contact or its hash.
/// </summary>
internal sealed class ConversationGuests(
    ConversationService store,
    IConversationDialect dialect,
    ConversationOptions options,
    ConversationGuestOptions guestOptions,
    IChallengeService challenges,
    TimeProvider time,
    ILogger<ConversationGuests> logger) : IConversationGuests, IGuestAccess
{
    private const int MaxExchangeAttempts = 3;
    private const int MaxInviteAttempts = 3;
    private const int MaxTokenLength = 256;

    /// <inheritdoc />
    public async Task<GuestInvite> InviteAsync(ConversationActor actor, string tenantId, Guid conversationId, GuestContact contact, string displayName, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));

        if (actor.Kind is not (ConversationActorKind.Member or ConversationActorKind.Partner)
            || !await store.AllowedAsync(actor, tenantId, conversationId, ConversationAction.Invite, cancellationToken))
        {
            return Failed(GuestInviteOutcome.Refused);
        }

        if (!GuestContactHasher.TryNormalise(contact, out var input) || !ConversationInput.IsDisplayName(displayName, options.MaxDisplayNameLength))
        {
            return Failed(GuestInviteOutcome.Invalid);
        }

        // Concurrent invites of one conversation can deadlock on the guest count; the statements are single, so the whole invite runs again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InviteOnceAsync(actor, tenantId, conversationId, input, displayName, cancellationToken);
            }
            catch (DbException ex) when (attempt < MaxInviteAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Conversation {ConversationId} guest invite lost a lock race; retrying (attempt {Attempt})", conversationId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<GuestInvite> InviteOnceAsync(ConversationActor actor, string tenantId, Guid conversationId, string input, string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        if (await store.ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is null)
        {
            return Failed(GuestInviteOutcome.Refused);
        }

        var found = await FindGuestAsync(connection, tenantId, conversationId, input, cancellationToken);
        if (found is null)
        {
            var participantId = Guid.NewGuid();
            var current = guestOptions.Peppers[guestOptions.CurrentPepperId!];
            try
            {
                var inserted = await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO conversation_participants (id, tenant_id, conversation_id, kind, display_name, contact_hash, pepper_key_id, joined_at, last_read_seq) " +
                    "SELECT @Id, @TenantId, c.id, 1, @DisplayName, @ContactHash, @PepperKeyId, @Now, 0 FROM conversations c " +
                    "WHERE c.tenant_id = @TenantId AND c.id = @ConversationId AND c.state = 0 AND c.retention_state <> 2 " +
                    "AND (SELECT COUNT(*) FROM conversation_participants g WHERE g.tenant_id = @TenantId AND g.conversation_id = @ConversationId AND g.kind = 1 AND g.left_at IS NULL) < @MaxGuests;",
                    new
                    {
                        Id = participantId,
                        TenantId = tenantId,
                        ConversationId = conversationId,
                        DisplayName = displayName,
                        ContactHash = GuestContactHasher.Hash(current, input),
                        PepperKeyId = guestOptions.CurrentPepperId,
                        Now = store.Now(),
                        MaxGuests = guestOptions.MaxGuestsPerConversation,
                    },
                    cancellationToken: cancellationToken));
                if (inserted == 0)
                {
                    return await ExplainNoInsertAsync(connection, tenantId, conversationId, cancellationToken);
                }

                return await IssueAsync(GuestInviteOutcome.Invited, participantId, tenantId, conversationId, cancellationToken);
            }
            catch (DbException ex) when (dialect.IsDuplicateKey(ex))
            {
                // A concurrent invite of the same contact won the unique index: this one is a re-invite of that guest.
                found = await FindGuestAsync(connection, tenantId, conversationId, input, cancellationToken)
                    ?? throw new InvalidOperationException("A duplicate guest contact was reported but no stored guest was found.");
            }
        }

        if (found.LeftAt is not null)
        {
            return Failed(GuestInviteOutcome.Refused); // a guest who left is not re-added by an invite
        }

        if (found.PepperKeyId != guestOptions.CurrentPepperId)
        {
            await RehashAsync(connection, tenantId, found, input, cancellationToken);
        }

        if (!await ConversationOpenAsync(connection, tenantId, conversationId, cancellationToken))
        {
            return Failed(GuestInviteOutcome.Refused);
        }

        return await IssueAsync(GuestInviteOutcome.Reinvited, found.Id, tenantId, conversationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GuestSessionResult> ExchangeAsync(string tenantId, string token, CancellationToken cancellationToken = default)
    {
        // Both arrive from an anonymous caller: a malformed value is a refusal, not an exception.
        var refused = new GuestSessionResult(GuestExchangeOutcome.Refused, null, null, null);
        if (!ConversationInput.IsTenant(tenantId) || string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return refused;
        }

        var verified = await challenges.VerifyByTokenAsync(token, ConversationGuestOptions.ChallengePurpose, tenantId, cancellationToken);
        if (verified.Outcome != ChallengeVerifyOutcome.Verified || !Guid.TryParse(verified.Scope.Key, out var participantId))
        {
            return refused;
        }

        // The token is spent now. A deadlock rolls the transaction back; running it again must not need the token.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await OpenSessionAsync(tenantId, participantId, cancellationToken) ?? refused;
            }
            catch (DbException ex) when (attempt < MaxExchangeAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Guest {ParticipantId} session exchange lost a lock race; retrying (attempt {Attempt})", participantId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<GuestSessionResult?> OpenSessionAsync(string tenantId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Lock the participant row first, so two redeems for one guest serialise here and "one live session" holds.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_participants SET last_read_seq = last_read_seq WHERE tenant_id = @TenantId AND id = @ParticipantId AND kind = 1 AND left_at IS NULL;",
            new { TenantId = tenantId, ParticipantId = participantId },
            transaction,
            cancellationToken: cancellationToken));
        var conversationId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT p.conversation_id FROM conversation_participants p JOIN conversations c ON c.tenant_id = p.tenant_id AND c.id = p.conversation_id " +
            "WHERE p.tenant_id = @TenantId AND p.id = @ParticipantId AND p.kind = 1 AND p.left_at IS NULL AND c.state = 0 AND c.retention_state <> 2;",
            new { TenantId = tenantId, ParticipantId = participantId },
            transaction,
            cancellationToken: cancellationToken));
        if (conversationId is not { } conversation)
        {
            return null;
        }

        var now = time.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_guest_sessions SET revoked_at = @Now WHERE tenant_id = @TenantId AND participant_id = @ParticipantId AND revoked_at IS NULL;",
            new { TenantId = tenantId, ParticipantId = participantId, Now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));

        var key = GuestSessionKey.Generate();
        var expiresAt = now + guestOptions.SessionLifetime;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO conversation_guest_sessions (id, tenant_id, conversation_id, participant_id, key_hash, created_at, expires_at) VALUES (@Id, @TenantId, @ConversationId, @ParticipantId, @KeyHash, @Now, @ExpiresAt);",
            new { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversation, ParticipantId = participantId, KeyHash = key.Hash(), Now = now.UtcDateTime, ExpiresAt = expiresAt.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Guest {ParticipantId} opened a session in conversation {ConversationId}", participantId, conversation);
        return new GuestSessionResult(GuestExchangeOutcome.Established, conversation, key, expiresAt);
    }

    private async Task<GuestInvite> IssueAsync(GuestInviteOutcome success, Guid participantId, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var issued = await challenges.IssueAsync(new ChallengeScope(participantId.ToString("D"), ConversationGuestOptions.ChallengePurpose, tenantId), cancellationToken);
        if (!issued.Succeeded)
        {
            logger.LogWarning("Conversation {ConversationId} guest {ParticipantId} link was rate limited", conversationId, participantId);
            return new GuestInvite(GuestInviteOutcome.RateLimited, participantId, null, null);
        }

        logger.LogInformation("Conversation {ConversationId} guest {ParticipantId} link issued ({Outcome})", conversationId, participantId, success);
        return new GuestInvite(success, participantId, issued.Secret, issued.ExpiresAt);
    }

    /// <summary>The guest of this conversation holding the contact, found under any configured pepper (the current one first).</summary>
    private async Task<GuestRow?> FindGuestAsync(DbConnection connection, string tenantId, Guid conversationId, string input, CancellationToken cancellationToken)
    {
        foreach (var id in guestOptions.Peppers.Keys.OrderBy(k => k == guestOptions.CurrentPepperId ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal))
        {
            var hash = GuestContactHasher.Hash(guestOptions.Peppers[id], input);
            var row = await connection.QuerySingleOrDefaultAsync<GuestRow>(new CommandDefinition(
                "SELECT id AS Id, left_at AS LeftAt, pepper_key_id AS PepperKeyId FROM conversation_participants WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND kind = 1 AND contact_hash = @Hash;",
                new { TenantId = tenantId, ConversationId = conversationId, Hash = hash },
                cancellationToken: cancellationToken));
            if (row is not null)
            {
                row.Hash = hash;
                return row;
            }
        }

        return null;
    }

    private async Task RehashAsync(DbConnection connection, string tenantId, GuestRow found, string input, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE conversation_participants SET contact_hash = @NewHash, pepper_key_id = @PepperKeyId WHERE tenant_id = @TenantId AND id = @Id AND contact_hash = @OldHash;",
                new { TenantId = tenantId, found.Id, OldHash = found.Hash, NewHash = GuestContactHasher.Hash(guestOptions.Peppers[guestOptions.CurrentPepperId!], input), PepperKeyId = guestOptions.CurrentPepperId },
                cancellationToken: cancellationToken));
        }
        catch (DbException ex) when (dialect.IsDuplicateKey(ex))
        {
            // A concurrent re-invite already moved this guest to the current key; the participant is the same.
        }
    }

    private static async Task<bool> ConversationOpenAsync(DbConnection connection, string tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM conversations WHERE tenant_id = @TenantId AND id = @ConversationId AND state = 0 AND retention_state <> 2;",
            new { TenantId = tenantId, ConversationId = conversationId },
            cancellationToken: cancellationToken)) == 1;

    private async Task<GuestInvite> ExplainNoInsertAsync(DbConnection connection, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        // The insert matched no row: either the conversation is not open (or not this tenant's) or it already holds the most guests.
        var open = await ConversationOpenAsync(connection, tenantId, conversationId, cancellationToken);
        return Failed(open ? GuestInviteOutcome.LimitReached : GuestInviteOutcome.Refused);
    }

    private static GuestInvite Failed(GuestInviteOutcome outcome) => new(outcome, null, null, null);

    private sealed class GuestRow
    {
        public Guid Id { get; set; }

        public object? LeftAt { get; set; }

        public string? PepperKeyId { get; set; }

        public byte[] Hash { get; set; } = [];
    }
}
