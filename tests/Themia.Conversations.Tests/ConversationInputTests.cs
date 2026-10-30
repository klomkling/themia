using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Conversations.DependencyInjection;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.Tests;

public sealed class ConversationInputTests
{
    private sealed class NoDatabase : IConversationDialect
    {
        public string LimitClause => throw new InvalidOperationException("the database must not be reached");

        public DbConnection CreateConnection() => throw new InvalidOperationException("the database must not be reached");

        public bool IsDuplicateKey(DbException exception) => false;

        public bool IsDeadlock(DbException exception) => false;

        public Task<long?> IncrementLastSeqAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database must not be reached");
    }

    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private static ConversationService Service() =>
        new(new NoDatabase(), new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), TimeProvider.System, NullLogger<ConversationService>.Instance);

    private static readonly ConversationActor Member = new(ConversationActorKind.Member, "user-1");

    [Theory]
    [InlineData("tenant ")]
    [InlineData(" tenant")]
    [InlineData("tenant\t")]
    [InlineData("")]
    public async Task OpenAsync_ShouldRejectATenantThatPaddingWouldMakeEqualToAnother_BeforeTouchingTheDatabase(string tenant)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().OpenAsync(Member, tenant, new ConversationSubject("booking", "B-1")));
    }

    [Fact]
    public async Task OpenAsync_ShouldRejectAPaddedOrOverlongSubject()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().OpenAsync(Member, "t", new ConversationSubject("booking", "B-1 ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().OpenAsync(Member, "t", new ConversationSubject("booking ", "B-1")));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().OpenAsync(Member, "t", new ConversationSubject("booking", new string('x', 201))));
    }

    [Fact]
    public async Task PostAsync_ShouldRejectAPaddedClientMessageId_AndAPaddedUserRef()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PostAsync(Member, "t", Guid.NewGuid(), new PostMessage("m-1 ", "hi")));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PostAsync(new ConversationActor(ConversationActorKind.Member, "user-1 "), "t", Guid.NewGuid(), new PostMessage("m-1", "hi")));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PostAsync(Member, "t", Guid.NewGuid(), new PostMessage(new string('x', 65), "hi")));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PostAsync(new ConversationActor(ConversationActorKind.Member, new string('x', 199)), "t", Guid.NewGuid(), new PostMessage("m-1", "hi")));
    }

    [Fact]
    public async Task ListAsync_ShouldRejectANegativeSeqAndAZeroLimit()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().ListAsync(Member, "t", Guid.NewGuid(), -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().ListAsync(Member, "t", Guid.NewGuid(), 0, 0));
    }

    [Fact]
    public void Outcomes_ShouldDeriveTheirBooleansFromTheEnum()
    {
        Assert.True(new PostResult(PostOutcome.Posted, Guid.NewGuid(), 1).Succeeded);
        Assert.True(new PostResult(PostOutcome.Duplicate, Guid.NewGuid(), 1).Succeeded);
        Assert.False(new PostResult(PostOutcome.Closed, null, null).Succeeded);
        Assert.False(new PostResult(PostOutcome.Refused, null, null).Succeeded);
        Assert.False(new PostResult(PostOutcome.InvalidMessage, null, null).Succeeded);
        Assert.True(new OpenResult(OpenOutcome.Reopened, Guid.NewGuid()).Succeeded);
        Assert.False(new OpenResult(OpenOutcome.PendingPurge, null).Succeeded);
        Assert.True(new JoinResult(JoinOutcome.AlreadyJoined, Guid.NewGuid()).Succeeded);
        Assert.False(new JoinResult(JoinOutcome.Invalid, null).Succeeded);
    }

    [Fact]
    public void AddThemiaConversations_ShouldRegisterTheServiceOverTheEnginesDialect()
    {
        var services = new ServiceCollection().AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>)).AddSingleton<IConversationDialect>(new NoDatabase()).AddThemiaConversations();

        Assert.IsType<ConversationService>(services.BuildServiceProvider().GetRequiredService<IConversationService>());
    }
}
