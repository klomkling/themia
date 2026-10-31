using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Conversations.Internal;
using Themia.Storage;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Retention end to end on a real PostgreSQL, MySQL and SQL Server: expire, claim, delete files then rows, hold.</summary>
public abstract class ConversationRetentionTests(ConversationEngineFixture fixture)
{
    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DenyActions(params ConversationAction[] denied) : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!denied.Contains(action));
    }

    private sealed class FakeHoldPort(params string[] heldSubjectIds) : ILegalHoldPort
    {
        public List<string> Asked { get; } = [];

        public ValueTask<bool> IsHeldAsync(string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default)
        {
            Asked.Add(subject.SubjectId);
            return ValueTask.FromResult(heldSubjectIds.Contains(subject.SubjectId));
        }
    }

    private sealed class ThrowingHoldPort : ILegalHoldPort
    {
        public ValueTask<bool> IsHeldAsync(string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("port down");
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

    private ConversationService Conversations() =>
        new(fixture.Dialect, new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance);

    private ConversationRetentionService Retention(
        ILegalHoldPort? port = null,
        IStorageProvider? storage = null,
        IConversationLifecycleSink? sink = null,
        IConversationAccessPolicy? policy = null,
        bool noPort = false) =>
        new(fixture.Dialect, new ConversationOptions { NoLegalHoldPort = noPort }, policy ?? new AllowAll(), sink ?? new DefaultConversationLifecycleSink(), port, storage, TimeProvider.System, NullLogger<ConversationRetentionService>.Instance);

    /// <summary>A conversation with two posted messages and one attachment; closed with a retention date in the past (or future).</summary>
    private async Task<(Guid Conversation, string Key)> ClosedConversationAsync(string tenant, string subjectId, bool past = true)
    {
        var service = Conversations();
        var opened = await service.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", subjectId));
        var conversation = opened.ConversationId!.Value;
        var joined = await service.JoinAsync(Member("a"), tenant, conversation, "Somchai");
        var first = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m1", "hello"));
        await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m2", "again"));
        var key = $"conv/{conversation:N}/file.bin";
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, content_type, size, sha256, created_at) VALUES (@Id, @Tenant, @Conversation, @Message, @Owner, @Key, 'image/png', 3, @Sha, @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Message = first.MessageId, Owner = joined.ParticipantId, Key = key, Sha = new string('a', 64), Now = DateTime.UtcNow });
        await service.CloseAsync(Member("a"), tenant, conversation);
        await fixture.ExecuteAsync(
            "UPDATE conversations SET retention_until = @Until WHERE id = @Id;",
            new { Id = conversation, Until = DateTime.UtcNow.AddDays(past ? -1 : 30) });
        return (conversation, key);
    }

    private Task<int> StateOfAsync(Guid conversation) => fixture.ScalarAsync<int>("SELECT retention_state FROM conversations WHERE id = @Id;", new { Id = conversation });

    private Task<int> CountAsync(string table, Guid conversation) =>
        fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE conversation_id = @Id;", new { Id = conversation });

    [Fact]
    public async Task Sweep_ShouldPurgeOnlyClosedConversationsPastRetention_AndWriteATombstone()
    {
        var tenant = NewTenant();
        var (due, key) = await ClosedConversationAsync(tenant, "B-due");
        var (notDue, _) = await ClosedConversationAsync(tenant, "B-later", past: false);
        var open = (await Conversations().OpenAsync(Member("o"), tenant, new ConversationSubject("booking", "B-open"))).ConversationId!.Value;
        var storage = new RecordingStorage();

        var result = await Retention(new FakeHoldPort(), storage).SweepAsync(tenant);

        Assert.Equal(new SweepResult(SweepOutcome.Completed, 1, 1, 0, 0), result);
        Assert.Equal([key], storage.Deleted);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = due }));
        foreach (var table in new[] { "conversation_participants", "conversation_messages", "conversation_attachments" })
        {
            Assert.Equal(0, await CountAsync(table, due));
        }

        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = notDue }));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = open }));
        Assert.Equal(0, await StateOfAsync(notDue));
        Assert.Equal(2, await fixture.ScalarAsync<int>("SELECT message_count FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = due }));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT attachment_count FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = due }));
    }

    [Fact]
    public async Task Sweep_ShouldFreeTheSubjectKey_SoTheSubjectOpensANewConversation()
    {
        var tenant = NewTenant();
        var (old, _) = await ClosedConversationAsync(tenant, "B-1");
        await Retention(new FakeHoldPort(), new RecordingStorage()).SweepAsync(tenant);

        var reopened = await Conversations().OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.Opened, reopened.Outcome);
        Assert.NotEqual(old, reopened.ConversationId);
    }

    [Fact]
    public async Task Sweep_ShouldPurgeAConversationWithoutAttachments()
    {
        var tenant = NewTenant();
        var service = Conversations();
        var id = (await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"))).ConversationId!.Value;
        await service.JoinAsync(Member("a"), tenant, id, "Somchai");
        await service.CloseAsync(Member("a"), tenant, id);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_until = @Until WHERE id = @Id;", new { Id = id, Until = DateTime.UtcNow.AddDays(-1) });

        var result = await Retention(new FakeHoldPort()).SweepAsync(tenant);

        Assert.Equal(1, result.Purged);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = id }));
    }

    [Fact]
    public async Task Sweep_ShouldReportHoldPortMissing_MarkExpired_AndPurgeNothing()
    {
        var tenant = NewTenant();
        var (conversation, key) = await ClosedConversationAsync(tenant, "B-1");
        var storage = new RecordingStorage();

        var result = await Retention(storage: storage).SweepAsync(tenant);

        Assert.Equal(new SweepResult(SweepOutcome.HoldPortMissing, 1, 0, 0, 0), result);
        Assert.False(result.Completed);
        Assert.Empty(storage.Deleted);
        Assert.Equal((int)ConversationRetentionState.Expired, await StateOfAsync(conversation));
        Assert.Equal(2, await CountAsync("conversation_messages", conversation));
        Assert.NotNull(key);
    }

    [Fact]
    public async Task Sweep_ShouldPurgeWithoutAPort_WhenTheOptionOptsOut()
    {
        var tenant = NewTenant();
        await ClosedConversationAsync(tenant, "B-1");

        var result = await Retention(storage: new RecordingStorage(), noPort: true).SweepAsync(tenant);

        Assert.Equal(1, result.Purged);
    }

    [Fact]
    public async Task Sweep_ShouldHoldAndSkip_WhenThePortSaysHeld()
    {
        var tenant = NewTenant();
        var (held, _) = await ClosedConversationAsync(tenant, "B-held");
        var (free, _) = await ClosedConversationAsync(tenant, "B-free");
        var storage = new RecordingStorage();

        var result = await Retention(new FakeHoldPort("B-held"), storage).SweepAsync(tenant);

        Assert.Equal(new SweepResult(SweepOutcome.Completed, 2, 1, 1, 0), result);
        Assert.Equal((int)ConversationRetentionState.LegalHold, await StateOfAsync(held));
        Assert.Equal(2, await CountAsync("conversation_messages", held));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = free }));
    }

    [Fact]
    public async Task Sweep_ShouldNeverUnHold_WhenThePortLaterSaysNotHeld()
    {
        var tenant = NewTenant();
        var (held, _) = await ClosedConversationAsync(tenant, "B-held");
        await Retention(new FakeHoldPort("B-held"), new RecordingStorage()).SweepAsync(tenant);

        var second = await Retention(new FakeHoldPort(), new RecordingStorage()).SweepAsync(tenant);

        Assert.Equal(0, second.Purged);
        Assert.Equal((int)ConversationRetentionState.LegalHold, await StateOfAsync(held));
    }

    [Fact]
    public async Task Sweep_ShouldTreatAPortFailureAsHeldOrUnknown_AndKeepTheConversation()
    {
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");

        var result = await Retention(new ThrowingHoldPort(), new RecordingStorage()).SweepAsync(tenant);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Purged);
        Assert.Equal((int)ConversationRetentionState.Expired, await StateOfAsync(conversation));
    }

    [Fact]
    public async Task Sweep_ShouldKeepTheRows_WhenAStorageDeleteFails_AndResumeWithoutAskingThePortAgain()
    {
        var tenant = NewTenant();
        var (conversation, key) = await ClosedConversationAsync(tenant, "B-1");
        var storage = new RecordingStorage();
        storage.FailOn.Add(key);
        var port = new FakeHoldPort();

        var first = await Retention(port, storage).SweepAsync(tenant);

        Assert.Equal(1, first.Failed);
        Assert.Equal((int)ConversationRetentionState.Purging, await StateOfAsync(conversation));
        Assert.Equal(2, await CountAsync("conversation_messages", conversation));

        storage.FailOn.Clear();
        port.Asked.Clear();
        var second = await Retention(port, storage).SweepAsync(tenant);

        Assert.Equal(1, second.Purged);
        Assert.Empty(port.Asked);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Sweep_ShouldNotTouchAnotherTenant()
    {
        var tenant = NewTenant();
        var other = NewTenant();
        var (mine, _) = await ClosedConversationAsync(tenant, "B-1");
        var (theirs, _) = await ClosedConversationAsync(other, "B-1");

        await Retention(new FakeHoldPort(), new RecordingStorage()).SweepAsync(tenant);

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = mine }));
        Assert.Equal(0, await StateOfAsync(theirs));
    }

    [Fact]
    public async Task Sweep_ShouldHonourTheBatchSize()
    {
        var tenant = NewTenant();
        await ClosedConversationAsync(tenant, "B-1");
        await ClosedConversationAsync(tenant, "B-2");
        await ClosedConversationAsync(tenant, "B-3");
        var retention = Retention(new FakeHoldPort(), new RecordingStorage());

        var first = await retention.SweepAsync(tenant, new SweepOptions(2));
        var second = await retention.SweepAsync(tenant, new SweepOptions(2));

        Assert.Equal(2, first.Purged);
        Assert.Equal(1, second.Purged);
    }

    [Fact]
    public async Task Sweep_ShouldRaiseAPurgedEventWithIdsAndCountsOnly_AndRollBackWhenTheSinkFails()
    {
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");
        var sink = new RecordingLifecycleSink { Throw = true };

        var failing = await Retention(new FakeHoldPort(), new RecordingStorage(), sink).SweepAsync(tenant);

        Assert.Equal(1, failing.Failed);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = conversation }));

        sink.Throw = false;
        sink.Seen.Clear();
        await Retention(new FakeHoldPort(), new RecordingStorage(), sink).SweepAsync(tenant);

        var raised = Assert.Single(sink.Seen);
        Assert.Equal(ConversationLifecycleKind.Purged, raised.Kind);
        Assert.Equal(conversation, raised.ConversationId);
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public async Task PlaceHold_ShouldHoldAnActiveAndAnExpiredConversation_AndReleaseShouldReturnItToActive()
    {
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");
        var retention = Retention();

        Assert.Equal(HoldOutcome.Placed, (await retention.PlaceHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal(HoldOutcome.AlreadyHeld, (await retention.PlaceHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal((int)ConversationRetentionState.LegalHold, await StateOfAsync(conversation));
        Assert.Equal(HoldOutcome.Released, (await retention.ReleaseHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal(HoldOutcome.NotHeld, (await retention.ReleaseHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal((int)ConversationRetentionState.Active, await StateOfAsync(conversation));

        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 2 WHERE id = @Id;", new { Id = conversation });
        Assert.Equal(HoldOutcome.Placed, (await retention.PlaceHoldAsync(Member("staff"), tenant, conversation)).Outcome);
    }

    [Fact]
    public async Task PlaceHold_ShouldBeRefused_ForAClaimedConversation_AnUnknownOne_AndAPolicyDenial()
    {
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 3 WHERE id = @Id;", new { Id = conversation });

        Assert.Equal(HoldOutcome.PurgeInProgress, (await Retention().PlaceHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal(HoldOutcome.NotFound, (await Retention().PlaceHoldAsync(Member("staff"), tenant, Guid.NewGuid())).Outcome);
        Assert.Equal(HoldOutcome.NotFound, (await Retention().PlaceHoldAsync(Member("staff"), NewTenant(), conversation)).Outcome);
        Assert.Equal(HoldOutcome.Refused, (await Retention(policy: new DenyActions(ConversationAction.Hold)).PlaceHoldAsync(Member("staff"), tenant, conversation)).Outcome);
        Assert.Equal(HoldOutcome.Refused, (await Retention(policy: new DenyActions(ConversationAction.ReleaseHold)).ReleaseHoldAsync(Member("staff"), tenant, conversation)).Outcome);
    }

    [Fact]
    public async Task DefaultRegistration_ShouldResolveRetentionAndErasure_AndPurgeNothingWithoutAHoldPort()
    {
        var retention = fixture.Services.GetRequiredService<IConversationRetention>();
        Assert.NotNull(fixture.Services.GetRequiredService<IConversationDataSubject>());
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");

        var result = await retention.SweepAsync(tenant);

        Assert.Equal(SweepOutcome.HoldPortMissing, result.Outcome);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Open_ShouldReportPendingPurge_WhileTheConversationIsBeingPurged()
    {
        var tenant = NewTenant();
        var (conversation, _) = await ClosedConversationAsync(tenant, "B-1");
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 3 WHERE id = @Id;", new { Id = conversation });

        var result = await Conversations().OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.PendingPurge, result.Outcome);
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationRetentionTests(PostgresConversationFixture fixture) : ConversationRetentionTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationRetentionTests(MySqlConversationFixture fixture) : ConversationRetentionTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationRetentionTests(SqlServerConversationFixture fixture) : ConversationRetentionTests(fixture);
