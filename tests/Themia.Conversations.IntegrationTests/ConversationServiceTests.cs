using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>The store, end to end on a real PostgreSQL, MySQL and SQL Server: ordering and idempotency under concurrency, tenant and conversation isolation, and the order policy, participant, idempotency read, counter, insert.</summary>
public abstract class ConversationServiceTests(ConversationEngineFixture fixture)
{
    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class RecordingPolicy : IConversationAccessPolicy
    {
        public List<ConversationAction> Seen { get; } = [];

        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default)
        {
            Seen.Add(action);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Allows opening only <c>B-1</c>, and nothing once a conversation id is named.</summary>
    private sealed class SubjectOnlyPolicy : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(action == ConversationAction.Open && target.ConversationId is null && target.Subject?.SubjectId == "B-1");
    }

    private sealed class ThrowingSink : IConversationEventSink
    {
        public Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationEvent conversationEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sink failed");
    }

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private ConversationService Service(IConversationAccessPolicy? policy = null, ConversationOptions? options = null, IConversationEventSink? sink = null) =>
        new(fixture.Dialect, options ?? new ConversationOptions(), policy ?? new AllowAll(), sink ?? new DefaultConversationEventSink(TimeProvider.System), TimeProvider.System, NullLogger<ConversationService>.Instance);

    private async Task<(string Tenant, Guid Conversation)> OpenAsync(ConversationService service, string? subjectId = null)
    {
        var tenant = NewTenant();
        var opened = await service.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", subjectId ?? "B-1"));
        return (tenant, opened.ConversationId!.Value);
    }

    private async Task JoinAsync(ConversationService service, string tenant, Guid conversation, string userRef)
    {
        var joined = await service.JoinAsync(Member(userRef), tenant, conversation, "Somchai");
        Assert.True(joined.Succeeded, joined.Outcome.ToString());
    }

    [Fact]
    public async Task Open_ShouldReturnTheSameConversation_WhenOpenedAgain()
    {
        var service = Service();
        var tenant = NewTenant();
        var subject = new ConversationSubject("booking", "B-1");

        var first = await service.OpenAsync(Member("a"), tenant, subject);
        var second = await service.OpenAsync(Member("a"), tenant, subject);

        Assert.Equal(OpenOutcome.Opened, first.Outcome);
        Assert.Equal(OpenOutcome.Opened, second.Outcome);
        Assert.Equal(first.ConversationId, second.ConversationId);
    }

    [Fact]
    public async Task Open_ShouldReopenAClosedConversation_AndClearItsRetentionDate()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await service.CloseAsync(Member("a"), tenant, conversation);

        var reopened = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.Reopened, reopened.Outcome);
        Assert.Equal(conversation, reopened.ConversationId);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT state FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(retention_until) FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Open_ShouldReportPendingPurge_WhenTheConversationExpired()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await fixture.ExecuteAsync("UPDATE conversations SET state = 1, retention_state = 2 WHERE id = @Id;", new { Id = conversation });

        var result = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.PendingPurge, result.Outcome);
        Assert.Null(result.ConversationId);
    }

    [Fact]
    public async Task Open_ShouldReportPendingPurge_WhenTheRetentionDateHasPassedBeforeThePurgeMarkedIt()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await fixture.ExecuteAsync("UPDATE conversations SET state = 1, retention_until = @Past WHERE id = @Id;", new { Id = conversation, Past = DateTime.UtcNow.AddDays(-1) });

        var result = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.PendingPurge, result.Outcome);
    }

    [Fact]
    public async Task Open_ShouldBeRefused_WhenThePolicyDenies_AndCreateNothing()
    {
        var tenant = NewTenant();
        var service = Service(policy: new DenyAllConversationAccessPolicy());

        var result = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.Refused, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE tenant_id = @Tenant;", new { Tenant = tenant }));
    }

    [Fact]
    public async Task Open_ShouldHandThePolicyTheSubject_AndRefuseAnotherSubject()
    {
        var service = Service(policy: new SubjectOnlyPolicy());
        var tenant = NewTenant();

        var other = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-2"));

        Assert.Equal(OpenOutcome.Refused, other.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversations WHERE tenant_id = @Tenant;", new { Tenant = tenant }));
    }

    [Fact]
    public async Task Open_ShouldNotRevealOrReopenAnExistingConversation_WhenThePolicyDeniesOnceItIsNamed()
    {
        var allow = Service();
        var (tenant, conversation) = await OpenAsync(allow);
        await JoinAsync(allow, tenant, conversation, "a");
        await allow.CloseAsync(Member("a"), tenant, conversation);
        var service = Service(policy: new SubjectOnlyPolicy());

        var result = await service.OpenAsync(Member("stranger"), tenant, new ConversationSubject("booking", "B-1"));

        Assert.Equal(OpenOutcome.Refused, result.Outcome);
        Assert.Null(result.ConversationId);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT state FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Open_ShouldNotMergeSubjectsThatDifferOnlyInCase()
    {
        var service = Service();
        var tenant = NewTenant();

        var upper = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1A"));
        var lower = await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-1a"));

        Assert.NotEqual(upper.ConversationId, lower.ConversationId);
    }

    [Fact]
    public async Task Join_ShouldCreateOneParticipant_WhenTheSamePersonJoinsConcurrently()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.JoinAsync(Member("a"), tenant, conversation, "Somchai"))));

        Assert.Equal(1, results.Count(r => r.Outcome == JoinOutcome.Joined));
        Assert.Equal(7, results.Count(r => r.Outcome == JoinOutcome.AlreadyJoined));
        Assert.Single(results.Select(r => r.ParticipantId).Distinct());
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Join_ShouldBeRefused_ForAnotherTenantsConversation()
    {
        var service = Service();
        var (_, conversation) = await OpenAsync(service);

        var result = await service.JoinAsync(Member("a"), NewTenant(), conversation, "Somchai");

        Assert.Equal(JoinOutcome.Refused, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<b>x</b>")]
    [InlineData("line\nbreak")]
    public async Task Join_ShouldReportInvalid_ForABadDisplayName(string name)
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);

        var result = await service.JoinAsync(Member("a"), tenant, conversation, name);

        Assert.Equal(JoinOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldGiveTheFirstMessageSeqOne_AndWriteTheEventInTheSameTransaction()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await JoinAsync(service, tenant, conversation, "b");

        var posted = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hello"));

        Assert.Equal(PostOutcome.Posted, posted.Outcome);
        Assert.Equal(1, posted.Seq);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT last_seq FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_events WHERE conversation_id = @Id;", new { Id = conversation }));
        var payload = await fixture.ScalarAsync<string>("SELECT payload FROM conversation_events WHERE conversation_id = @Id;", new { Id = conversation });
        Assert.DoesNotContain("hello", payload, StringComparison.Ordinal);
        Assert.Contains(posted.MessageId!.Value.ToString(), payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_ShouldGiveDenseSequenceNumbers_UnderConcurrentPosters()
    {
        const int posters = 20;
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        for (var i = 0; i < posters; i++) await JoinAsync(service, tenant, conversation, "u" + i);

        var results = await Task.WhenAll(Enumerable.Range(0, posters).Select(i =>
            Task.Run(() => service.PostAsync(Member("u" + i), tenant, conversation, new PostMessage("m-1", "hi " + i)))));

        Assert.All(results, r => Assert.Equal(PostOutcome.Posted, r.Outcome));
        Assert.Equal(Enumerable.Range(1, posters).Select(i => (long)i), results.Select(r => r.Seq!.Value).Order());
        Assert.Equal(posters, await fixture.ScalarAsync<long>("SELECT last_seq FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(posters, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Post_ShouldStoreOnce_WhenTheSameClientMessageIdIsPostedConcurrently()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => service.PostAsync(Member("a"), tenant, conversation, new PostMessage("retry-1", "hello")))));

        Assert.Equal(1, results.Count(r => r.Outcome == PostOutcome.Posted));
        Assert.Equal(9, results.Count(r => r.Outcome == PostOutcome.Duplicate));
        Assert.Single(results.Select(r => r.MessageId).Distinct());
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT last_seq FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_events WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Post_ShouldReturnTheOriginal_WhenTheDuplicateCarriesADifferentBody()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        var first = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "original"));

        var second = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "changed"));

        Assert.Equal(PostOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal("original", await fixture.ScalarAsync<string>("SELECT body FROM conversation_messages WHERE id = @Id;", new { Id = first.MessageId }));
    }

    [Fact]
    public async Task Post_ShouldNotTreatClientMessageIdsThatDifferOnlyInCaseAsDuplicates()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var lower = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("abc", "one"));
        var upper = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("ABC", "two"));

        Assert.Equal(PostOutcome.Posted, lower.Outcome);
        Assert.Equal(PostOutcome.Posted, upper.Outcome);
    }

    [Fact]
    public async Task Post_ShouldBeClosed_WhenTheConversationIsClosed()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await service.CloseAsync(Member("a"), tenant, conversation);

        var result = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "late"));

        Assert.Equal(PostOutcome.Closed, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Post_ShouldBeRefused_ForSomeoneWhoNeverJoined()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);

        var result = await service.PostAsync(Member("stranger"), tenant, conversation, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldBeRefused_ForAParticipantWhoLeft()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await fixture.ExecuteAsync("UPDATE conversation_participants SET left_at = @Now WHERE conversation_id = @Id;", new { Id = conversation, Now = DateTime.UtcNow });

        var result = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldBeRefused_WhenAGuestActorNamesAMembersParticipantId()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        var member = await service.JoinAsync(Member("a"), tenant, conversation, "Somchai");

        var asGuest = new ConversationActor(ConversationActorKind.Guest, ParticipantId: member.ParticipantId);
        var result = await service.PostAsync(asGuest, tenant, conversation, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldBeRefused_ForAParticipantOfAnotherConversation()
    {
        var service = Service();
        var (tenant, conversationA) = await OpenAsync(service, "B-1");
        var conversationB = (await service.OpenAsync(Member("a"), tenant, new ConversationSubject("booking", "B-2"))).ConversationId!.Value;
        await JoinAsync(service, tenant, conversationB, "a");

        var result = await service.PostAsync(Member("a"), tenant, conversationA, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldBeRefused_ForAnotherTenant_AndWriteNothing()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var result = await service.PostAsync(Member("a"), NewTenant(), conversation, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task Post_ShouldBeRefusedByThePolicy_BeforeAnythingIsRead()
    {
        var allow = Service();
        var (tenant, conversation) = await OpenAsync(allow);
        await JoinAsync(allow, tenant, conversation, "a");
        var deny = Service(policy: new DenyAllConversationAccessPolicy());

        var result = await deny.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hi"));

        Assert.Equal(PostOutcome.Refused, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT last_seq FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task EveryMethod_ShouldAskThePolicyForItsOwnAction()
    {
        var policy = new RecordingPolicy();
        var service = Service(policy);
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hi"));
        await service.ListAsync(Member("a"), tenant, conversation, 0, 10);
        await service.MarkReadAsync(Member("a"), tenant, conversation, 1);
        await service.CloseAsync(Member("a"), tenant, conversation);

        Assert.Equal(
            [ConversationAction.Open, ConversationAction.Join, ConversationAction.Post, ConversationAction.Read, ConversationAction.MarkRead, ConversationAction.Close],
            policy.Seen);
    }

    [Fact]
    public async Task Post_ShouldRollBackTheMessageAndTheCounter_WhenTheEventSinkFails()
    {
        var good = Service();
        var (tenant, conversation) = await OpenAsync(good);
        await JoinAsync(good, tenant, conversation, "a");
        var failing = Service(sink: new ThrowingSink());

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hi")));

        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT last_seq FROM conversations WHERE id = @Id;", new { Id = conversation }));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation }));
        var retry = await good.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "hi"));
        Assert.Equal(1, retry.Seq);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_ShouldReportInvalidMessage_ForAnEmptyBody(string body)
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var result = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", body));

        Assert.Equal(PostOutcome.InvalidMessage, result.Outcome);
    }

    [Fact]
    public async Task Post_ShouldReportInvalidMessage_ForABodyOverTheCap_AndAcceptOneAtTheCap()
    {
        var service = Service(options: new ConversationOptions { MaxBodyLength = 10 });
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var over = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", new string('x', 11)));
        var exact = await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-2", new string('x', 10)));

        Assert.Equal(PostOutcome.InvalidMessage, over.Outcome);
        Assert.Equal(PostOutcome.Posted, exact.Outcome);
    }

    [Fact]
    public async Task List_ShouldReturnMessagesInSeqOrder_AfterTheGivenSeq_CappedByTheOption()
    {
        var service = Service(options: new ConversationOptions { MaxListLimit = 3 });
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        for (var i = 1; i <= 5; i++) await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-" + i, "body " + i));

        var firstPage = await service.ListAsync(Member("a"), tenant, conversation, 0, 50);
        var secondPage = await service.ListAsync(Member("a"), tenant, conversation, 3, 50);

        Assert.Equal([1L, 2L, 3L], firstPage.Messages.Select(m => m.Seq));
        Assert.Equal([4L, 5L], secondPage.Messages.Select(m => m.Seq));
        Assert.Equal("body 4", secondPage.Messages[0].Body);
        Assert.Equal(DateTimeKind.Utc, secondPage.Messages[0].CreatedAt.UtcDateTime.Kind);
        Assert.InRange(secondPage.Messages[0].CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task List_ShouldBeRefused_ForANonParticipant_AndForAnotherTenant()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-1", "secret"));

        var stranger = await service.ListAsync(Member("stranger"), tenant, conversation, 0, 10);
        var otherTenant = await service.ListAsync(Member("a"), NewTenant(), conversation, 0, 10);

        Assert.Equal(ListOutcome.Refused, stranger.Outcome);
        Assert.Empty(stranger.Messages);
        Assert.Equal(ListOutcome.Refused, otherTenant.Outcome);
    }

    [Fact]
    public async Task MarkRead_ShouldNeverLowerTheMarker_AndClampToTheConversationsLastSeq()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");
        for (var i = 1; i <= 3; i++) await service.PostAsync(Member("a"), tenant, conversation, new PostMessage("m-" + i, "x"));

        var two = await service.MarkReadAsync(Member("a"), tenant, conversation, 2);
        var lowerAgain = await service.MarkReadAsync(Member("a"), tenant, conversation, 1);
        var beyond = await service.MarkReadAsync(Member("a"), tenant, conversation, 999);

        Assert.Equal(2, two.LastReadSeq);
        Assert.Equal(2, lowerAgain.LastReadSeq);
        Assert.Equal(3, beyond.LastReadSeq);
    }

    [Fact]
    public async Task MarkRead_ShouldBeRefused_ForANonParticipant()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);

        var result = await service.MarkReadAsync(Member("stranger"), tenant, conversation, 1);

        Assert.Equal(MarkReadOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Close_ShouldBeIdempotent_AndSetTheRetentionDateFromTheSubjectTypesOption()
    {
        var options = new ConversationOptions();
        options.RetentionBySubjectType["booking"] = TimeSpan.FromDays(30);
        var service = Service(options: options);
        var (tenant, conversation) = await OpenAsync(service);
        await JoinAsync(service, tenant, conversation, "a");

        var first = await service.CloseAsync(Member("a"), tenant, conversation);
        var second = await service.CloseAsync(Member("a"), tenant, conversation);

        Assert.Equal(CloseOutcome.Closed, first.Outcome);
        Assert.Equal(CloseOutcome.AlreadyClosed, second.Outcome);
        var until = await fixture.ScalarAsync<object>("SELECT retention_until FROM conversations WHERE id = @Id;", new { Id = conversation });
        var days = (ConversationTime.ToUtc(until) - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(days, 29.9, 30.1);
    }

    [Fact]
    public async Task Close_ShouldBeRefused_ForANonParticipant()
    {
        var service = Service();
        var (tenant, conversation) = await OpenAsync(service);

        var result = await service.CloseAsync(Member("stranger"), tenant, conversation);

        Assert.Equal(CloseOutcome.Refused, result.Outcome);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT state FROM conversations WHERE id = @Id;", new { Id = conversation }));
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationServiceTests(PostgresConversationFixture fixture) : ConversationServiceTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationServiceTests(MySqlConversationFixture fixture) : ConversationServiceTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationServiceTests(SqlServerConversationFixture fixture) : ConversationServiceTests(fixture);
