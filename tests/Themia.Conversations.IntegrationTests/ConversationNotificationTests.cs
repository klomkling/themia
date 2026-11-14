using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>The notification drain end to end on a real PostgreSQL, MySQL and SQL Server: window, cursor, lease, covered events, locks.</summary>
public abstract class ConversationNotificationTests(ConversationEngineFixture fixture)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class RecordingSubscriber : IConversationNotificationSubscriber
    {
        public List<ConversationNotification> Seen { get; } = [];

        public HashSet<Guid> ThrowFor { get; } = [];

        public Func<ConversationNotification, Task>? Hook { get; set; }

        public async Task NotifyAsync(ConversationNotification notification, CancellationToken cancellationToken = default)
        {
            if (ThrowFor.Contains(notification.ParticipantId))
            {
                throw new InvalidOperationException("channel down");
            }

            if (Hook is not null)
            {
                await Hook(notification);
            }

            Seen.Add(notification);
        }
    }

    private sealed class ThrowingSubscriber(string message) : IConversationNotificationSubscriber
    {
        public Task NotifyAsync(ConversationNotification notification, CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add($"{formatter(state, exception)} | {exception}");
    }

    private sealed record Room(string Tenant, Guid Conversation, Guid Poster, Guid A, Guid B);

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private ConversationService Conversations(TimeProvider time) =>
        new(fixture.Dialect, new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(time), new RegexConversationMasker(), time, NullLogger<ConversationService>.Instance);

    private static ConversationNotificationService Drain(IConversationDialect dialect, FakeTimeProvider time, IConversationNotificationSubscriber? subscriber) =>
        new(dialect, new ConversationOptions { NotifyDebounce = Window }, subscriber, time, NullLogger<ConversationNotificationService>.Instance);

    private ConversationNotificationService Drain(FakeTimeProvider time, IConversationNotificationSubscriber? subscriber) => Drain(fixture.Dialect, time, subscriber);

    /// <summary>A conversation with a poster and two recipients, A and B.</summary>
    private async Task<Room> RoomAsync(FakeTimeProvider time)
    {
        var tenant = NewTenant();
        var service = Conversations(time);
        var conversation = (await service.OpenAsync(Member("poster"), tenant, new ConversationSubject("booking", "B-1"))).ConversationId!.Value;
        var poster = (await service.JoinAsync(Member("poster"), tenant, conversation, "Poster")).ParticipantId!.Value;
        var a = (await service.JoinAsync(Member("a"), tenant, conversation, "Anan")).ParticipantId!.Value;
        var b = (await service.JoinAsync(Member("b"), tenant, conversation, "Boon")).ParticipantId!.Value;
        return new Room(tenant, conversation, poster, a, b);
    }

    private async Task PostAsync(FakeTimeProvider time, Room room, string clientId) =>
        Assert.True((await Conversations(time).PostAsync(Member("poster"), room.Tenant, room.Conversation, new PostMessage(clientId, "hello"))).Succeeded);

    private Task<bool> EventDeliveredAsync(Room room) =>
        fixture.ScalarAsync<bool>("SELECT CASE WHEN COUNT(*) = COUNT(delivered_at) THEN 1 ELSE 0 END FROM conversation_events WHERE tenant_id = @Tenant AND conversation_id = @Conversation;", new { room.Tenant, room.Conversation });

    [Fact]
    public async Task Drain_ShouldSendOneBatchPerRecipient_OnlyAfterTheWindow_AndNotCountRedacted()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        for (var i = 1; i <= 20; i++)
        {
            await PostAsync(time, room, $"m{i}");
        }

        await fixture.ExecuteAsync("UPDATE conversation_messages SET redacted_at = @Now WHERE tenant_id = @Tenant AND conversation_id = @Conversation AND seq = 20;", new { room.Tenant, room.Conversation, Now = time.GetUtcNow().UtcDateTime });
        var subscriber = new RecordingSubscriber();
        var drain = Drain(time, subscriber);

        await drain.DrainAsync();
        Assert.DoesNotContain(subscriber.Seen, n => n.ConversationId == room.Conversation);
        time.Advance(Window + TimeSpan.FromSeconds(1));
        await drain.DrainAsync();

        Assert.Equal(2, subscriber.Seen.Count(n => n.ConversationId == room.Conversation));
        var forA = Assert.Single(subscriber.Seen, n => n.ParticipantId == room.A);
        Assert.Equal(19, forA.UnreadCount);
        Assert.Equal(19, forA.LastSeq);
        Assert.DoesNotContain(subscriber.Seen, n => n.ParticipantId == room.Poster);
    }

    [Fact]
    public async Task Drain_ShouldNotPushASucceededRecipientAgain_WhenAnotherThrows_AndRetryTheFailedOne()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var subscriber = new RecordingSubscriber();
        subscriber.ThrowFor.Add(room.B);
        var drain = Drain(time, subscriber);

        await drain.DrainAsync();
        Assert.DoesNotContain(subscriber.Seen, n => n.ParticipantId == room.B);
        subscriber.ThrowFor.Clear();
        await drain.DrainAsync();

        Assert.Single(subscriber.Seen, n => n.ParticipantId == room.A);
        Assert.Single(subscriber.Seen, n => n.ParticipantId == room.B);
    }

    [Fact]
    public async Task Drain_ShouldSendNothing_ToARecipientWhoRead_UntilTheWindowAfterTheNextPost()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        await Conversations(time).MarkReadAsync(Member("a"), room.Tenant, room.Conversation, 1);
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var subscriber = new RecordingSubscriber();
        var drain = Drain(time, subscriber);

        await drain.DrainAsync();
        await PostAsync(time, room, "m2");
        await drain.DrainAsync();

        Assert.DoesNotContain(subscriber.Seen, n => n.ParticipantId == room.A);
        time.Advance(Window + TimeSpan.FromSeconds(1));
        await drain.DrainAsync();
        Assert.Contains(subscriber.Seen, n => n.ParticipantId == room.A && n.LastSeq == 2);
    }

    [Fact]
    public async Task Drain_ShouldMarkTheEventDelivered_WhenOneRecipientLeftAndTheOtherWasNotified()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        await fixture.ExecuteAsync("UPDATE conversation_participants SET left_at = @Now WHERE tenant_id = @Tenant AND id = @Id;", new { room.Tenant, Id = room.B, Now = time.GetUtcNow().UtcDateTime });
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var drain = Drain(time, new RecordingSubscriber());

        await drain.DrainAsync();
        await drain.DrainAsync();

        Assert.True(await EventDeliveredAsync(room));
    }

    [Fact]
    public async Task Drain_ShouldMarkTheEventDelivered_WhenTheOnlyUnreadMessageIsRedacted()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        await fixture.ExecuteAsync("UPDATE conversation_messages SET redacted_at = @Now WHERE tenant_id = @Tenant AND conversation_id = @Conversation;", new { room.Tenant, room.Conversation, Now = time.GetUtcNow().UtcDateTime });
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var subscriber = new RecordingSubscriber();

        await Drain(time, subscriber).DrainAsync();

        Assert.Empty(subscriber.Seen);
        Assert.True(await EventDeliveredAsync(room));
    }

    [Fact]
    public async Task Drain_ShouldNotBlockMarkRead_WhileAClaimHoldsTheCursorRow()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_notify_cursors (tenant_id, conversation_id, participant_id, notified_seq, created_at, updated_at) VALUES (@Tenant, @Conversation, @A, 0, @Now, @Now);",
            new { room.Tenant, room.Conversation, A = room.A, Now = time.GetUtcNow().UtcDateTime });

        await using var holder = fixture.Dialect.CreateConnection();
        await holder.OpenAsync();
        await using var claim = await holder.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await Dapper.SqlMapper.QueryAsync<Guid>(holder,
            $"SELECT n.participant_id FROM conversation_notify_cursors n {fixture.Dialect.CursorClaimTableHint} " +
            "JOIN conversation_participants p ON p.tenant_id = n.tenant_id AND p.conversation_id = n.conversation_id AND p.id = n.participant_id " +
            $"WHERE n.tenant_id = @Tenant AND n.conversation_id = @Conversation {fixture.Dialect.CursorClaimRowLockClause};",
            new { room.Tenant, room.Conversation },
            claim);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var read = await Conversations(time).MarkReadAsync(Member("a"), room.Tenant, room.Conversation, 1, timeout.Token);

        Assert.Equal(1, read.LastReadSeq);
        await claim.RollbackAsync();
    }

    [Fact]
    public async Task Drain_ShouldNotNotifyTwice_WhenASecondDrainRunsWhileTheFirstIsInTheSubscriber()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var second = new RecordingSubscriber();
        var inner = new List<DrainResult>();
        var first = new RecordingSubscriber
        {
            Hook = async _ => inner.Add(await Drain(time, second).DrainAsync()),
        };

        await Drain(time, first).DrainAsync();

        Assert.Equal(2, first.Seen.Count(n => n.ConversationId == room.Conversation));
        Assert.DoesNotContain(second.Seen, n => n.ConversationId == room.Conversation);
        Assert.NotEmpty(inner);
    }

    [Fact]
    public async Task Drain_ShouldNotCountDelivered_WhenTheLeaseWasTakenOverBeforeTheMark()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var second = new RecordingSubscriber();
        var reclaimed = false;
        var first = new RecordingSubscriber
        {
            Hook = async n =>
            {
                if (reclaimed || n.ParticipantId != room.A)
                {
                    return;
                }

                reclaimed = true;
                time.Advance(TimeSpan.FromMinutes(6));
                await Drain(time, second).DrainAsync();
            },
        };

        var outer = await Drain(time, first).DrainAsync();

        Assert.True(outer.LeaseLost >= 1);
        Assert.Contains(second.Seen, n => n.ParticipantId == room.A);
    }

    [Fact]
    public async Task Drain_ShouldDeliverEachRecipientOnce_WhenTwoDrainsStartWithNoCursorRows()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var subscriber = new RecordingSubscriber();
        var gate = new object();
        var safe = new RecordingSubscriber { Hook = n => { lock (gate) { subscriber.Seen.Add(n); } return Task.CompletedTask; } };

        await Task.WhenAll(Drain(time, safe).DrainAsync(), Drain(time, safe).DrainAsync());

        Assert.Equal(1, subscriber.Seen.Count(n => n.ParticipantId == room.A));
        Assert.Equal(1, subscriber.Seen.Count(n => n.ParticipantId == room.B));
    }

    [Fact]
    public async Task Drain_ShouldReportSubscriberMissing_AndTouchNoRow()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));

        var result = await Drain(time, null).DrainAsync();

        Assert.Equal(DrainOutcome.SubscriberMissing, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_notify_cursors WHERE tenant_id = @Tenant;", new { room.Tenant }));
    }

    [Fact]
    public async Task Drain_ShouldNotLogTheSubscriberExceptionMessage_WhenTheSubscriberThrows()
    {
        const string Marker = "someone@example.com +66 81 234 5678";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var room = await RoomAsync(time);
        await PostAsync(time, room, "m1");
        time.Advance(Window + TimeSpan.FromSeconds(1));
        var logger = new CapturingLogger<ConversationNotificationService>();
        var drain = new ConversationNotificationService(
            fixture.Dialect, new ConversationOptions { NotifyDebounce = Window }, new ThrowingSubscriber(Marker), time, logger);

        await drain.DrainAsync();

        Assert.Contains(logger.Entries, e => e.Contains(room.A.ToString(), StringComparison.OrdinalIgnoreCase) && e.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Contains("example.com", StringComparison.Ordinal) || e.Contains("5678", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Drain_ShouldThrow_WhenTheBatchSizeIsOutOfRange(int batchSize)
    {
        var drain = Drain(new FakeTimeProvider(DateTimeOffset.UtcNow), new RecordingSubscriber());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => drain.DrainAsync(new DrainOptions(batchSize)));
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationNotificationTests(PostgresConversationFixture fixture) : ConversationNotificationTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationNotificationTests(MySqlConversationFixture fixture) : ConversationNotificationTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationNotificationTests(SqlServerConversationFixture fixture) : ConversationNotificationTests(fixture);
