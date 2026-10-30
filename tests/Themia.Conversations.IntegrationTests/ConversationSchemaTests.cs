using System.Data.Common;
using Themia.Conversations.Migrations;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>
/// The first migration is forward-only, so these tests pin what is expensive to change later: the keys, the foreign-key shape, the byte-exact collation
/// and the unique constraints, on a real PostgreSQL, MySQL and SQL Server.
/// </summary>
public abstract class ConversationSchemaTests(ConversationEngineFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private async Task<Guid> ConversationAsync(string tenant, string subjectType = "booking", string? subjectId = null)
    {
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO conversations (id, tenant_id, subject_type, subject_id, state, retention_state, last_seq, created_at) VALUES (@Id, @Tenant, @SubjectType, @SubjectId, 0, 0, 0, @Now);",
            new { Id = id, Tenant = tenant, SubjectType = subjectType, SubjectId = subjectId ?? Guid.NewGuid().ToString("N"), Now });
        return id;
    }

    private async Task<Guid> ParticipantAsync(string tenant, Guid conversation, string? userRef = null, byte[]? contactHash = null)
    {
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_participants (id, tenant_id, conversation_id, kind, user_ref, display_name, contact_hash, joined_at, last_read_seq) VALUES (@Id, @Tenant, @Conversation, 0, @UserRef, 'Somchai', @ContactHash, @Now, 0);",
            new { Id = id, Tenant = tenant, Conversation = conversation, UserRef = userRef, ContactHash = contactHash, Now });
        return id;
    }

    private async Task<Guid> MessageAsync(string tenant, Guid conversation, Guid participant, long seq, string clientMessageId = "m-1")
    {
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_messages (id, tenant_id, conversation_id, seq, participant_id, kind, body, client_message_id, created_at) VALUES (@Id, @Tenant, @Conversation, @Seq, @Participant, 0, 'hello', @ClientMessageId, @Now);",
            new { Id = id, Tenant = tenant, Conversation = conversation, Seq = seq, Participant = participant, ClientMessageId = clientMessageId, Now });
        return id;
    }

    private Task<DbException> RefusedAsync(Func<Task> statement) => Assert.ThrowsAnyAsync<DbException>(statement);

    [Fact]
    public async Task Migration_ShouldCreateAllSevenTables()
    {
        foreach (var table in new[] { "conversations", "conversation_participants", "conversation_messages", "conversation_attachments", "conversation_guest_sessions", "conversation_events", "conversation_tombstones" })
        {
            Assert.True(await fixture.TableExistsAsync(table), table);
        }
    }

    [Fact]
    public async Task Migration_ShouldCreateTheKeysIndexesAndForeignKeys()
    {
        foreach (var index in new[]
                 {
                     "ux_conversations_subject", "ux_conversations_tenant_id", "ux_conversation_participants_key", "ux_conversation_participants_user_ref",
                     "ux_conversation_participants_contact", "ix_conversation_participants_contact_lookup", "ix_conversation_participants_user_ref_lookup",
                     "ux_conversation_messages_seq", "ux_conversation_messages_key", "ux_conversation_messages_idempotency",
                     "ix_conversation_attachments_message", "ix_conversation_attachments_owner", "ux_conversation_guest_sessions_key_hash",
                     "ix_conversation_guest_sessions_participant", "ix_conversation_events_conversation", "ix_conversation_events_undelivered",
                     "ix_conversation_tombstones_subject",
                 })
        {
            Assert.True(await fixture.IndexExistsAsync(index), index);
        }

        foreach (var fk in new[]
                 {
                     "fk_conversation_participants_conversation", "fk_conversation_messages_participant", "fk_conversation_messages_conversation",
                     "fk_conversation_attachments_message", "fk_conversation_attachments_owner", "fk_conversation_guest_sessions_participant",
                 })
        {
            Assert.True(await fixture.ForeignKeyExistsAsync(fk), fk);
        }
    }

    [Fact]
    public void Migration_ShouldRunAgainWithoutError() =>
        ThemiaMigrations.Run(fixture.MigrationAdapter, fixture.ConnectionString, typeof(ConversationSchemaMigration).Assembly);

    [Fact]
    public async Task Migration_ShouldRecordItselfInThisAssemblysOwnLedger()
    {
        var ledger = new ThemiaVersionTable(typeof(ConversationSchemaMigration).Assembly).TableName;
        Assert.True(await fixture.TableExistsAsync(ledger), $"expected ledger table {ledger}");
    }

    [Fact]
    public async Task Participant_ShouldBeRefused_WhenItsTenantDiffersFromItsConversations()
    {
        var conversation = await ConversationAsync("tenant-a-" + Guid.NewGuid().ToString("N"));

        await RefusedAsync(() => ParticipantAsync("tenant-b-" + Guid.NewGuid().ToString("N"), conversation));
    }

    [Fact]
    public async Task Participant_ShouldBeRefused_WhenTheTenantDiffersOnlyByCase()
    {
        // Red without the binary collation on MySQL and SQL Server: "Acme" and "acme" would be the same tenant there.
        var suffix = Guid.NewGuid().ToString("N");
        var conversation = await ConversationAsync("Acme-" + suffix);

        await RefusedAsync(() => ParticipantAsync("acme-" + suffix, conversation));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE tenant_id = @Tenant;", new { Tenant = "acme-" + suffix }));
    }

    [Fact]
    public async Task Message_ShouldBeRefused_WhenItsAuthorBelongsToAnotherConversationOfTheSameTenant()
    {
        var tenant = NewTenant();
        var one = await ConversationAsync(tenant);
        var two = await ConversationAsync(tenant);
        var authorOfTwo = await ParticipantAsync(tenant, two);

        await RefusedAsync(() => MessageAsync(tenant, one, authorOfTwo, 1));
    }

    [Fact]
    public async Task Attachment_ShouldBeRefused_WhenItsOwnerBelongsToAnotherConversationOfTheSameTenant()
    {
        var tenant = NewTenant();
        var one = await ConversationAsync(tenant);
        var two = await ConversationAsync(tenant);
        var authorOfOne = await ParticipantAsync(tenant, one);
        var ownerInTwo = await ParticipantAsync(tenant, two);
        var message = await MessageAsync(tenant, one, authorOfOne, 1);

        await RefusedAsync(() => fixture.ExecuteAsync(
            "INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, content_type, size, sha256, created_at) VALUES (@Id, @Tenant, @Conversation, @Message, @Owner, 'k', 'image/png', 1, @Sha, @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = one, Message = message, Owner = ownerInTwo, Sha = new string('a', 64), Now }));
    }

    [Fact]
    public async Task Subject_ShouldBeUniquePerTenant_ButCaseSensitive()
    {
        var tenant = NewTenant();
        await ConversationAsync(tenant, "booking", "B-1a");

        await ConversationAsync(tenant, "booking", "B-1A");    // a different subject, not a duplicate of the line above
        await RefusedAsync(() => ConversationAsync(tenant, "booking", "B-1a"));
        await ConversationAsync(NewTenant(), "booking", "B-1a");   // another tenant may use the same subject
    }

    [Fact]
    public async Task Participant_ShouldBeUniquePerPersonAndConversation_SoARepeatedJoinLeavesOneRow()
    {
        var tenant = NewTenant();
        var conversation = await ConversationAsync(tenant);
        var other = await ConversationAsync(tenant);
        var hash = new byte[32]; hash[0] = 7;

        await ParticipantAsync(tenant, conversation, userRef: "u-1");
        await RefusedAsync(() => ParticipantAsync(tenant, conversation, userRef: "u-1"));
        await ParticipantAsync(tenant, other, userRef: "u-1");                 // same person, another conversation

        await ParticipantAsync(tenant, conversation, contactHash: hash);
        await RefusedAsync(() => ParticipantAsync(tenant, conversation, contactHash: hash));

        await ParticipantAsync(tenant, conversation);                          // NULL user_ref and NULL contact_hash never collide
        await ParticipantAsync(tenant, conversation);
    }

    [Fact]
    public async Task Message_ShouldRefuseARepeatedSeqAndARepeatedClientMessageId_ButNotOneThatDiffersOnlyByCase()
    {
        var tenant = NewTenant();
        var conversation = await ConversationAsync(tenant);
        var author = await ParticipantAsync(tenant, conversation, userRef: "u-1");

        await MessageAsync(tenant, conversation, author, 1, "abc");
        await RefusedAsync(() => MessageAsync(tenant, conversation, author, 1, "other"));    // seq is unique per conversation
        await RefusedAsync(() => MessageAsync(tenant, conversation, author, 2, "abc"));      // idempotency key
        await MessageAsync(tenant, conversation, author, 3, "ABC");                         // red without the collation: a false duplicate
    }

    [Fact]
    public async Task Message_ShouldHaveAPrimaryKeyOnItsId()
    {
        // MySQL names every primary key index PRIMARY, so the index-name check cannot see it; a repeated id can.
        var tenant = NewTenant();
        var conversation = await ConversationAsync(tenant);
        var author = await ParticipantAsync(tenant, conversation, userRef: "u-1");
        var id = await MessageAsync(tenant, conversation, author, 1);

        await RefusedAsync(() => fixture.ExecuteAsync(
            "INSERT INTO conversation_messages (id, tenant_id, conversation_id, seq, participant_id, kind, body, client_message_id, created_at) VALUES (@Id, @Tenant, @Conversation, 2, @Participant, 0, 'x', 'm-2', @Now);",
            new { Id = id, Tenant = tenant, Conversation = conversation, Participant = author, Now }));
    }

    [Fact]
    public async Task Conversation_ShouldBeDeletableOnlyAfterItsChildren()
    {
        var tenant = NewTenant();
        var conversation = await ConversationAsync(tenant);
        var author = await ParticipantAsync(tenant, conversation, userRef: "u-1");
        await MessageAsync(tenant, conversation, author, 1);

        await RefusedAsync(() => fixture.ExecuteAsync("DELETE FROM conversations WHERE id = @Id;", new { Id = conversation }));

        await fixture.ExecuteAsync("DELETE FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation });
        await fixture.ExecuteAsync("DELETE FROM conversation_participants WHERE conversation_id = @Id;", new { Id = conversation });
        Assert.Equal(1, await fixture.ExecuteAsync("DELETE FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task EventAndTombstone_ShouldNotNeedTheConversationRowToExist()
    {
        var tenant = NewTenant();
        var gone = Guid.NewGuid();

        await fixture.ExecuteAsync(
            "INSERT INTO conversation_events (id, tenant_id, conversation_id, kind, payload, created_at) VALUES (@Id, @Tenant, @Conversation, 0, '{}', @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = gone, Now });
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_tombstones (conversation_id, tenant_id, subject_type, subject_id, message_count, attachment_count, purged_at) VALUES (@Conversation, @Tenant, 'booking', 'B-1', 3, 1, @Now);",
            new { Conversation = gone, Tenant = tenant, Now });
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationSchemaTests(PostgresConversationFixture fixture) : ConversationSchemaTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationSchemaTests(MySqlConversationFixture fixture) : ConversationSchemaTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationSchemaTests(SqlServerConversationFixture fixture) : ConversationSchemaTests(fixture);
