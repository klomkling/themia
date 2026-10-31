using System.Data.Common;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Conversations.Internal;
using Themia.Storage;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Per-person erasure end to end on a real PostgreSQL, MySQL and SQL Server.</summary>
public abstract class ConversationErasureTests(ConversationEngineFixture fixture)
{
    private static readonly byte[] CurrentPepper = Encoding.UTF8.GetBytes("current-pepper-0123456789-0123456789");
    private static readonly byte[] OldPepper = Encoding.UTF8.GetBytes("old-pepper-0123456789-0123456789-xyz");

    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DenyErase : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(action != ConversationAction.Erase);
    }

    private sealed class RecordingStorage : IStorageProvider
    {
        public List<string> Deleted { get; } = [];

        public HashSet<string> FailOn { get; } = [];

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailOn.Contains(key))
            {
                throw new IOException("disk gone");
            }

            Deleted.Add(key);
            return Task.CompletedTask;
        }

        public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Uri GetPublicUrl(string key) => throw new NotSupportedException();
    }

    private sealed class RecordingLifecycleSink : IConversationLifecycleSink
    {
        public List<ConversationLifecycleEvent> Seen { get; } = [];

        public bool Throw { get; set; }

        public Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
        {
            Seen.Add(lifecycleEvent);
            return Throw ? throw new InvalidOperationException("sink failed") : Task.CompletedTask;
        }
    }

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private static ConversationGuestOptions GuestOptions()
    {
        var options = new ConversationGuestOptions { CurrentPepperId = "p2" };
        options.Peppers["p1"] = OldPepper;
        options.Peppers["p2"] = CurrentPepper;
        return options;
    }

    private ConversationService Conversations() =>
        new(fixture.Dialect, new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance);

    private ConversationDataSubjectService Subject(RecordingStorage? storage = null, IConversationLifecycleSink? sink = null, IConversationAccessPolicy? policy = null, bool guests = true) =>
        new(fixture.Dialect, policy ?? new AllowAll(), sink ?? new DefaultConversationLifecycleSink(), guests ? GuestOptions() : null, storage ?? new RecordingStorage(), TimeProvider.System, NullLogger<ConversationDataSubjectService>.Instance);

    private sealed record Room(Guid Conversation, Guid Alice, Guid Bob, string AliceKey, Guid AliceFirstMessage);

    /// <summary>A room with alice (two messages, one attachment) and bob (one message), alice also with a guest session row.</summary>
    private async Task<Room> RoomAsync(string tenant, string subjectId = "B-1", string aliceRef = "alice")
    {
        var service = Conversations();
        var conversation = (await service.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", subjectId))).ConversationId!.Value;
        var alice = (await service.JoinAsync(Member(aliceRef), tenant, conversation, "Alice")).ParticipantId!.Value;
        var bob = (await service.JoinAsync(Member("bob"), tenant, conversation, "Bob")).ParticipantId!.Value;
        var first = await service.PostAsync(Member(aliceRef), tenant, conversation, new PostMessage("a1", "alice one"));
        await service.PostAsync(Member("bob"), tenant, conversation, new PostMessage("b1", "bob one"));
        await service.PostAsync(Member(aliceRef), tenant, conversation, new PostMessage("a2", "alice two"));
        var key = $"conv/{conversation:N}/alice.bin";
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, content_type, size, sha256, created_at) VALUES (@Id, @Tenant, @Conversation, @Message, @Owner, @Key, 'image/png', 3, @Sha, @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Message = first.MessageId, Owner = alice, Key = key, Sha = new string('a', 64), Now = DateTime.UtcNow });
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_guest_sessions (id, tenant_id, conversation_id, participant_id, key_hash, created_at, expires_at) VALUES (@Id, @Tenant, @Conversation, @Participant, @Hash, @Now, @Later);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Participant = alice, Hash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Now = DateTime.UtcNow, Later = DateTime.UtcNow.AddDays(1) });
        return new Room(conversation, alice, bob, key, first.MessageId!.Value);
    }

    private Task<int> CountAsync(string sql, object parameters) => fixture.ScalarAsync<int>(sql, parameters);

    [Fact]
    public async Task Erase_ShouldRedactOnlyThePersonsMessages_KeepTheSequence_AndAnonymiseTheParticipant()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();

        var result = await Subject(storage).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(ErasureOutcome.Erased, result.Outcome);
        Assert.Equal(1, result.Conversations);
        Assert.Equal(2, result.MessagesRedacted);
        Assert.Equal(1, result.AttachmentsDeleted);
        Assert.Equal([room.AliceKey], storage.Deleted);
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body IS NULL AND redacted_at IS NOT NULL;", new { Id = room.Conversation }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body = 'bob one';", new { Id = room.Conversation }));
        Assert.Equal("1,2,3", string.Join(',', await SeqsAsync(room.Conversation)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_attachments WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_guest_sessions WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_participants WHERE id = @Id AND user_ref IS NOT NULL;", new { Id = room.Alice }));
        Assert.Equal(ConversationDataSubjectService.ErasedDisplayName, await fixture.ScalarAsync<string>("SELECT display_name FROM conversation_participants WHERE id = @Id;", new { Id = room.Alice }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(left_at) FROM conversation_participants WHERE id = @Id;", new { Id = room.Alice }));
        Assert.Equal("Bob", await fixture.ScalarAsync<string>("SELECT display_name FROM conversation_participants WHERE id = @Id;", new { Id = room.Bob }));
    }

    private async Task<List<long>> SeqsAsync(Guid conversation)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq FROM conversation_messages WHERE conversation_id = @Id ORDER BY seq;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Id";
        parameter.Value = conversation;
        command.Parameters.Add(parameter);
        var seqs = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs;
    }

    [Fact]
    public async Task Erase_ShouldBeIdempotent_AWrongOrErasedPersonFindsNothing()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);
        var subject = Subject();
        await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        var again = await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));
        var stranger = await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("nobody"));

        Assert.Equal(ErasureOutcome.Erased, again.Outcome);
        Assert.Equal(0, again.Conversations + again.MessagesRedacted + again.AttachmentsDeleted);
        Assert.Empty(again.DeferredConversationIds);
        Assert.Empty(again.FailedConversationIds);
        Assert.Equal(ErasureOutcome.Erased, stranger.Outcome);
        Assert.Equal(0, stranger.Conversations);
    }

    [Fact]
    public async Task Erase_ShouldDefer_AConversationUnderLegalHold_AndLeaveItUntouched()
    {
        var tenant = NewTenant();
        var held = await RoomAsync(tenant, "B-held");
        var free = await RoomAsync(tenant, "B-free");
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 1 WHERE id = @Id;", new { Id = held.Conversation });
        var storage = new RecordingStorage();

        var result = await Subject(storage).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(ErasureOutcome.PartiallyErased, result.Outcome);
        Assert.False(result.Complete);
        Assert.Equal([held.Conversation], result.DeferredConversationIds);
        Assert.Equal([free.AliceKey], storage.Deleted);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM conversation_attachments WHERE conversation_id = @Id;", new { Id = held.Conversation }));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body IS NOT NULL AND participant_id = @P;", new { Id = held.Conversation, P = held.Alice }));
    }

    [Fact]
    public async Task Erase_ShouldReportDeferred_WhenEveryConversationIsOnHold()
    {
        var tenant = NewTenant();
        var held = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 1 WHERE id = @Id;", new { Id = held.Conversation });

        var result = await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(ErasureOutcome.Deferred, result.Outcome);
        Assert.Equal(0, result.Conversations);
    }

    [Fact]
    public async Task Erase_ShouldLeaveRowsUntouched_WhenAFileDeleteFails_AndSucceedOnRetry()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        storage.FailOn.Add(room.AliceKey);
        var subject = Subject(storage);

        var first = await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal([room.Conversation], first.FailedConversationIds);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM conversation_attachments WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body IS NULL;", new { Id = room.Conversation }));

        storage.FailOn.Clear();
        var second = await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(ErasureOutcome.Erased, second.Outcome);
        Assert.Equal(2, second.MessagesRedacted);
    }

    [Fact]
    public async Task Erase_ShouldNotTouchTheSameUserRefInAnotherTenant_OrAPartnerWithTheSameId()
    {
        var tenant = NewTenant();
        var other = NewTenant();
        await RoomAsync(tenant);
        var theirs = await RoomAsync(other);
        var partnerConversation = (await Conversations().OpenAsync(Member("o"), tenant, new ConversationSubject("booking", "B-partner"))).ConversationId!.Value;
        await Conversations().JoinAsync(new ConversationActor(ConversationActorKind.Partner, "alice"), tenant, partnerConversation, "Partner Alice");

        await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body IS NOT NULL AND participant_id = @P;", new { Id = theirs.Conversation, P = theirs.Alice }));
        Assert.Equal("Partner Alice", await fixture.ScalarAsync<string>("SELECT display_name FROM conversation_participants WHERE conversation_id = @Id;", new { Id = partnerConversation }));
    }

    [Fact]
    public async Task Erase_ShouldFindAGuestByContact_UnderAnOlderPepper()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var guest = Guid.NewGuid();
        var hash = GuestContactHasher.Hash(OldPepper, "email:guest@example.com");
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_participants (id, tenant_id, conversation_id, kind, display_name, contact_hash, pepper_key_id, joined_at, last_read_seq) VALUES (@Id, @Tenant, @Conversation, 1, 'Guest', @Hash, 'p1', @Now, 0);",
            new { Id = guest, Tenant = tenant, Conversation = room.Conversation, Hash = hash, Now = DateTime.UtcNow });

        var result = await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForGuest(new GuestContact(GuestContactKind.Email, "Guest@Example.com")));

        Assert.Equal(1, result.Conversations);
        Assert.Equal(ConversationDataSubjectService.ErasedDisplayName, await fixture.ScalarAsync<string>("SELECT display_name FROM conversation_participants WHERE id = @Id;", new { Id = guest }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(contact_hash) FROM conversation_participants WHERE id = @Id;", new { Id = guest }));
    }

    [Fact]
    public async Task Erase_ShouldBeRefused_ByThePolicy_AndInvalid_ForABadReference()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);

        var refused = await Subject(policy: new DenyErase()).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));
        var badContact = await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForGuest(new GuestContact(GuestContactKind.Phone, "0812345678")));
        var noGuests = await Subject(guests: false).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForGuest(new GuestContact(GuestContactKind.Email, "a@b.co")));
        var blank = await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember(" "));

        Assert.Equal(ErasureOutcome.Refused, refused.Outcome);
        Assert.Equal(ErasureOutcome.Invalid, badContact.Outcome);
        Assert.Equal(ErasureOutcome.Invalid, noGuests.Outcome);
        Assert.Equal(ErasureOutcome.Invalid, blank.Outcome);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE tenant_id = @T AND body IS NULL;", new { T = tenant }));
    }

    [Fact]
    public async Task Erase_ShouldNotDeadlockOrLeakAMessage_WhenThePersonPostsConcurrently()
    {
        var tenant = NewTenant();
        var service = Conversations();
        var conversation = (await service.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", "B-1"))).ConversationId!.Value;
        await service.JoinAsync(Member("alice"), tenant, conversation, "Alice");
        await service.JoinAsync(Member("bob"), tenant, conversation, "Bob");
        var subject = Subject();

        var posts = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            service.PostAsync(Member(i % 2 == 0 ? "alice" : "bob"), tenant, conversation, new PostMessage($"m{i}", $"text {i}")))).ToList();
        var erase = Task.Run(() => subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice")));
        await Task.WhenAll(posts.Cast<Task>().Append(erase));

        Assert.Equal(ErasureOutcome.Erased, (await erase).Outcome);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_messages m JOIN conversation_participants p ON p.id = m.participant_id WHERE m.conversation_id = @Id AND p.user_ref IS NULL AND p.display_name <> 'Bob' AND m.body IS NOT NULL;", new { Id = conversation }));
    }

    [Fact]
    public async Task Erase_ShouldRaiseAnEventWithIdsAndCountsOnly_AndRollBackWhenTheSinkFails()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var sink = new RecordingLifecycleSink { Throw = true };

        var failing = await Subject(sink: sink).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal([room.Conversation], failing.FailedConversationIds);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id AND body IS NULL;", new { Id = room.Conversation }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(user_ref) FROM conversation_participants WHERE id = @Id;", new { Id = room.Alice }));

        sink.Throw = false;
        sink.Seen.Clear();
        await Subject(sink: sink).ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        var raised = Assert.Single(sink.Seen);
        Assert.Equal(ConversationLifecycleKind.ParticipantErased, raised.Kind);
        Assert.Equal(room.Alice, raised.ParticipantId);
        Assert.Equal(2, raised.Count);
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationErasureTests(PostgresConversationFixture fixture) : ConversationErasureTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationErasureTests(MySqlConversationFixture fixture) : ConversationErasureTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationErasureTests(SqlServerConversationFixture fixture) : ConversationErasureTests(fixture);
