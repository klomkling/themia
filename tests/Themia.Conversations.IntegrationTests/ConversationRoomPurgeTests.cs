using Themia.Conversations.Internal;
using Themia.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Room-scoped erase (coord #0244) on a real PostgreSQL, MySQL and SQL Server.</summary>
public abstract partial class ConversationErasureTests
{
    private sealed class DenyRoomErase : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(action != ConversationAction.PurgeConversation);
    }

    private sealed class FakeHold(bool held) : ILegalHoldPort
    {
        public ValueTask<bool> IsHeldAsync(string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default) => ValueTask.FromResult(held);
    }

    /// <summary>Answers from a script, one entry per ask (the last repeats); an entry may also run a side effect or throw, to stage a hold that lands between two asks.</summary>
    private sealed class ScriptedHold(params Func<bool>[] script) : ILegalHoldPort
    {
        public int Asks { get; private set; }

        public ValueTask<bool> IsHeldAsync(string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(script[Math.Min(Asks++, script.Length - 1)]());
    }

    private ConversationDataSubjectService Eraser(IStorageProvider? storage, ILegalHoldPort? hold = null, ConversationOptions? options = null, IConversationAccessPolicy? policy = null) =>
        new(fixture.Dialect, policy ?? new AllowAll(), new DefaultConversationLifecycleSink(), null, storage, TimeProvider.System, NullLogger<ConversationDataSubjectService>.Instance, hold, options ?? new ConversationOptions { NoLegalHoldPort = hold is null });

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    private Task<int> StateAsync(Guid conversation) => CountAsync("SELECT retention_state FROM conversations WHERE id = @Id;", new { Id = conversation });

    private Task<int> RowsAsync(Guid conversation) =>
        CountAsync("SELECT (SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id) + (SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id) + (SELECT COUNT(*) FROM conversation_attachments WHERE conversation_id = @Id) + (SELECT COUNT(*) FROM conversations WHERE id = @Id);", new { Id = conversation });

    [Fact]
    public async Task PurgeConversation_ShouldDeleteTheRoomOnly_AndReturnCounts()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var other = await RoomAsync(tenant, "B-2", "carol");
        var storage = new RecordingStorage();

        var result = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(new ConversationPurgeResult(ConversationPurgeOutcome.Purged, 3, 1), result);
        Assert.Equal(0, await RowsAsync(room.Conversation));
        Assert.Equal([room.AliceKey], storage.Deleted);
        Assert.True(await RowsAsync(other.Conversation) > 0);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id AND message_count = 3 AND attachment_count = 1;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldReturnNotFoundWithTheTombstoneCounts_WhenAskedTwice()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await Eraser(new RecordingStorage()).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        var again = await Eraser(new RecordingStorage()).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);
        var wrongTenant = await Eraser(new RecordingStorage()).PurgeConversationAsync(Member("staff"), NewTenant(), room.Conversation);

        Assert.Equal(new ConversationPurgeResult(ConversationPurgeOutcome.NotFound, 3, 1), again);
        Assert.Equal(ConversationPurgeOutcome.NotFound, wrongTenant.Outcome);
    }

    [Fact]
    public async Task PurgeConversation_ShouldDeferAndKeepEverything_WhenTheRoomIsOnHold()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 1 WHERE id = @Id;", new { Id = room.Conversation });
        var storage = new RecordingStorage();

        var result = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Deferred, result.Outcome);
        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await StateAsync(room.Conversation));
    }

    [Fact]
    public async Task PurgeConversation_ShouldDeferAndMarkTheHold_WhenThePortSaysHeldButTheMirrorLags()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();

        var result = await Eraser(storage, new FakeHold(true)).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Deferred, result.Outcome);
        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await StateAsync(room.Conversation));
    }

    [Fact]
    public async Task PurgeConversation_ShouldDefer_WhenNoHoldPortAndNotOptedOut()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);

        var result = await Eraser(new RecordingStorage(), options: new ConversationOptions()).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Deferred, result.Outcome);
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversation_ShouldResumeAPurgingRoom()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 3 WHERE id = @Id;", new { Id = room.Conversation });

        var result = await Eraser(new RecordingStorage()).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Purged, result.Outcome);
        Assert.Equal(0, await RowsAsync(room.Conversation));
    }

    [Fact]
    public async Task PurgeConversation_ShouldFailAndStayPurging_WhenAFileCannotBeDeleted_ThenFinishOnRetry()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        storage.FailOn.Add(room.AliceKey);

        var failed = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Failed, failed.Outcome);
        Assert.Equal(3, await StateAsync(room.Conversation));
        Assert.True(await RowsAsync(room.Conversation) > 0);

        storage.FailOn.Clear();
        var retried = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Purged, retried.Outcome);
        Assert.Equal(0, await RowsAsync(room.Conversation));
    }

    [Fact]
    public async Task PurgeConversation_ShouldFail_WhenThereAreFilesAndNoStorage()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);

        var result = await Eraser(storage: null).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Failed, result.Outcome);
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversation_ShouldRefuseAndTouchNothing_WhenPolicyDenies()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();

        var result = await Eraser(storage, policy: new DenyRoomErase()).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Refused, result.Outcome);
        Assert.Empty(storage.Deleted);
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversationBySubject_ShouldReturnNotFoundAndCreateNothing_WhenThereIsNoRoom()
    {
        var tenant = NewTenant();

        var result = await Eraser(new RecordingStorage()).PurgeConversationBySubjectAsync(Member("staff"), tenant, new ConversationSubject("booking", "none"));

        Assert.Equal(ConversationPurgeOutcome.NotFound, result.Outcome);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversations WHERE tenant_id = @T;", new { T = tenant }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE tenant_id = @T;", new { T = tenant }));
    }

    [Fact]
    public async Task PurgeConversationBySubject_ShouldEraseTheRoom()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant, "B-9");

        var result = await Eraser(new RecordingStorage()).PurgeConversationBySubjectAsync(Member("staff"), tenant, new ConversationSubject("booking", "B-9"));

        Assert.Equal(ConversationPurgeOutcome.Purged, result.Outcome);
        Assert.Equal(0, await RowsAsync(room.Conversation));
    }

    [Fact]
    public async Task Post_ShouldBeRefused_WhenTheRoomIsPurging()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 3 WHERE id = @Id;", new { Id = room.Conversation });
        var before = await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = room.Conversation });

        var post = await Conversations().PostAsync(Member("alice"), tenant, room.Conversation, new PostMessage("late", "too late"));

        Assert.Equal(PostOutcome.Closed, post.Outcome);
        Assert.Equal(before, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldDeferAndGiveTheRoomBack_WhileAnUploadIsInFlight_ThenEraseOnceTheGraceIsOver()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var staging = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachment_uploads (id, tenant_id, conversation_id, owner_participant_id, storage_key, created_at) VALUES (@Id, @T, @C, @O, @K, @Now);",
            new { Id = staging, T = tenant, C = room.Conversation, O = room.Alice, K = $"conv/{room.Conversation:N}/staged.bin", Now = DateTime.UtcNow.AddSeconds(-1) });
        var storage = new RecordingStorage();

        var deferred = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Deferred, deferred.Outcome);
        Assert.Empty(storage.Deleted);
        Assert.Equal(0, await StateAsync(room.Conversation));

        await fixture.ExecuteAsync("UPDATE conversation_attachment_uploads SET created_at = @Old WHERE id = @Id;", new { Id = staging, Old = DateTime.UtcNow.AddHours(-2) });
        var erased = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Purged, erased.Outcome);
        Assert.Contains($"conv/{room.Conversation:N}/staged.bin", storage.Deleted);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_attachment_uploads WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldLeaveNoRows_WhenPostsRaceTheErase()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var erase = Eraser(new RecordingStorage());
        var service = Conversations();

        var posts = Enumerable.Range(0, 8).Select(i => service.PostAsync(Member("alice"), tenant, room.Conversation, new PostMessage($"r{i}", "race")));
        var results = await Task.WhenAll(Task.Run(async () => (await erase.PurgeConversationAsync(Member("staff"), tenant, room.Conversation)).Outcome), Task.Run(async () =>
        {
            await Task.WhenAll(posts);
            return ConversationPurgeOutcome.Purged;
        }));

        var final = await erase.PurgeConversationAsync(Member("staff"), tenant, room.Conversation);
        Assert.True(results[0] is ConversationPurgeOutcome.Purged or ConversationPurgeOutcome.Deferred or ConversationPurgeOutcome.Failed);
        Assert.True(final.Outcome is ConversationPurgeOutcome.Purged or ConversationPurgeOutcome.NotFound);
        Assert.Equal(0, await RowsAsync(room.Conversation));
    }

    [Fact]
    public async Task PurgeConversation_ShouldResolveFromTheDefaultContainer_AndRefuseByDefault()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var subject = fixture.Services.GetRequiredService<IConversationDataSubject>();

        var result = await subject.PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Refused, result.Outcome); // the default policy denies every action, the new one included
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversation_ShouldLogIdsAndCountsOnly()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        storage.FailOn.Add(room.AliceKey);
        var logger = new CapturingLogger<ConversationDataSubjectService>();
        var service = new ConversationDataSubjectService(fixture.Dialect, new AllowAll(), new DefaultConversationLifecycleSink(), null, storage, TimeProvider.System, logger, null, new ConversationOptions { NoLegalHoldPort = true });

        await service.PurgeConversationAsync(Member("staff"), tenant, room.Conversation);
        storage.FailOn.Clear();
        await service.PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        var log = string.Join('\n', logger.Lines);
        Assert.NotEmpty(logger.Lines);
        foreach (var secret in new[] { "alice one", "bob one", "alice two", "Alice", "Bob", "alice.bin", room.AliceKey })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PurgeConversation_ShouldApplyTheHold_WhenTheRoomIsAlreadyPurging()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 3 WHERE id = @Id;", new { Id = room.Conversation });
        var storage = new RecordingStorage();

        var result = await Eraser(storage, new FakeHold(true)).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.True(result.Held);
        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await StateAsync(room.Conversation));
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task DeleteRows_ShouldDeleteNothingAndKeepTheHold_WhenTheRoomWasHeldAfterTheClaim()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 1 WHERE id = @Id;", new { Id = room.Conversation });
        var purger = new ConversationPurger(fixture.Dialect, new DefaultConversationLifecycleSink(), null, new RecordingStorage(), TimeProvider.System, NullLogger.Instance);

        var rows = await purger.DeleteRowsAsync(tenant, room.Conversation, new ConversationSubject("booking", "B-1"), CancellationToken.None);

        Assert.Equal(PurgeRowsOutcome.LostClaim, rows.Outcome);
        Assert.Equal(1, await StateAsync(room.Conversation));
        Assert.True(await RowsAsync(room.Conversation) > 0);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldKeepTheMessagesAndHold_WhenTheHoldArrivesAfterTheFilesWereDeleted()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        var hold = new ScriptedHold(() => false, () => false, () => true); // eraser, files, rows

        var result = await Eraser(storage, hold).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.True(result.Held);
        Assert.Equal(3, hold.Asks);
        Assert.Equal([room.AliceKey], storage.Deleted);
        Assert.Equal(1, await StateAsync(room.Conversation));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldDeleteNoFile_WhenTheHoldArrivesAfterTheClaim()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        var hold = new ScriptedHold(() => false, () => true); // eraser, files

        var result = await Eraser(storage, hold).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.True(result.Held);
        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await StateAsync(room.Conversation));
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversation_ShouldAnswerNotFound_WhenAnotherFinisherDeletedTheRoomBeforeTheReask()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var finisher = Eraser(new RecordingStorage());
        var hold = new ScriptedHold(
            () => false,
            () =>
            {
                // A second caller resumes the claim and finishes the purge while this one is between its claim and its file step.
                var done = finisher.PurgeConversationAsync(Member("staff"), tenant, room.Conversation).GetAwaiter().GetResult();
                Assert.Equal(ConversationPurgeOutcome.Purged, done.Outcome);
                return true;
            });

        var result = await Eraser(new RecordingStorage(), hold).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.NotFound, result.Outcome);
        Assert.Equal(3, result.MessagesDeleted);
    }

    [Fact]
    public async Task PurgeConversation_ShouldFailAndStayClaimed_WhenThePortThrowsOnTheReask()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        var hold = new ScriptedHold(() => false, () => throw new InvalidOperationException("port down"));

        var result = await Eraser(storage, hold).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Failed, result.Outcome);
        Assert.Empty(storage.Deleted);
        Assert.Equal(3, await StateAsync(room.Conversation));
        Assert.True(await RowsAsync(room.Conversation) > 0);
    }

    [Fact]
    public async Task PurgeConversation_ShouldFailAndKeepTheMessages_WhenThePortThrowsOnTheRowsReask()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var storage = new RecordingStorage();
        var hold = new ScriptedHold(() => false, () => false, () => throw new InvalidOperationException("port down"));

        var result = await Eraser(storage, hold).PurgeConversationAsync(Member("staff"), tenant, room.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Failed, result.Outcome);
        Assert.Equal([room.AliceKey], storage.Deleted);
        Assert.Equal(3, await StateAsync(room.Conversation));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = room.Conversation }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task PurgeConversation_ShouldLeaveAnotherTenantsRoomUntouched_WhenBothShareTheSubject()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var roomA = await RoomAsync(tenantA);
        var roomB = await RoomAsync(tenantB);
        var storage = new RecordingStorage();

        var byId = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenantA, roomA.Conversation);

        Assert.Equal(ConversationPurgeOutcome.Purged, byId.Outcome);
        Assert.Equal([roomA.AliceKey], storage.Deleted);
        Assert.Equal(0, await RowsAsync(roomA.Conversation));
        await AssertUntouchedAsync(roomB);
    }

    [Fact]
    public async Task PurgeConversationBySubject_ShouldLeaveAnotherTenantsRoomUntouched_WhenBothShareTheSubject()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var roomA = await RoomAsync(tenantA);
        var roomB = await RoomAsync(tenantB);
        var storage = new RecordingStorage();

        var result = await Eraser(storage).PurgeConversationBySubjectAsync(Member("staff"), tenantA, new ConversationSubject("booking", "B-1"));

        Assert.Equal(ConversationPurgeOutcome.Purged, result.Outcome);
        Assert.Equal([roomA.AliceKey], storage.Deleted);
        await AssertUntouchedAsync(roomB);
    }

    [Fact]
    public async Task PurgeConversation_ShouldAnswerNotFoundAndTouchNothing_WhenTheIdBelongsToAnotherTenant()
    {
        var tenantA = NewTenant();
        var roomB = await RoomAsync(NewTenant());
        var storage = new RecordingStorage();

        var result = await Eraser(storage).PurgeConversationAsync(Member("staff"), tenantA, roomB.Conversation);

        Assert.Equal(ConversationPurgeOutcome.NotFound, result.Outcome);
        Assert.Empty(storage.Deleted);
        await AssertUntouchedAsync(roomB);
    }

    private async Task AssertUntouchedAsync(Room room)
    {
        Assert.True(await RowsAsync(room.Conversation) > 0);
        Assert.Equal(0, await StateAsync(room.Conversation));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM conversation_tombstones WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }
}
