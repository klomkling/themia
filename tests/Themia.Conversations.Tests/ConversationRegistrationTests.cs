using Microsoft.Extensions.DependencyInjection;
using Themia.Conversations.DependencyInjection;
using Xunit;

namespace Themia.Conversations.Tests;

public sealed class ConversationRegistrationTests
{
    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    public static TheoryData<ConversationActorKind, ConversationAction> EveryActorAndAction()
    {
        var data = new TheoryData<ConversationActorKind, ConversationAction>();
        foreach (var kind in Enum.GetValues<ConversationActorKind>())
            foreach (var action in Enum.GetValues<ConversationAction>())
                data.Add(kind, action);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryActorAndAction))]
    public async Task DefaultPolicy_ShouldDenyEverything_SoAnUnconfiguredAppFailsClosed(ConversationActorKind kind, ConversationAction action)
    {
        var provider = new ServiceCollection().AddThemiaConversations().BuildServiceProvider();
        var policy = provider.GetRequiredService<IConversationAccessPolicy>();

        var allowed = await policy.CanAsync(new ConversationActor(kind, "user-1"), new ConversationTarget("tenant-1", Guid.NewGuid()), action);

        Assert.False(allowed);
    }

    [Fact]
    public async Task AnAppsOwnPolicy_ShouldWin_WhetherRegisteredBeforeOrAfter()
    {
        foreach (var before in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (before) services.AddSingleton<IConversationAccessPolicy, AllowAll>();
            services.AddThemiaConversations();
            if (!before) services.AddSingleton<IConversationAccessPolicy, AllowAll>();

            var policy = services.BuildServiceProvider().GetRequiredService<IConversationAccessPolicy>();

            Assert.True(await policy.CanAsync(new ConversationActor(ConversationActorKind.Member), new ConversationTarget("t", null), ConversationAction.Open));
        }
    }

    [Fact]
    public void AddThemiaConversations_ShouldFailAtRegistration_WhenAnOptionIsOutOfRange() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaConversations(o => o.MaxListLimit = 0));

    [Fact]
    public void AddThemiaConversations_ShouldRegisterTheConfiguredOptions()
    {
        var provider = new ServiceCollection().AddThemiaConversations(o => o.MaxListLimit = 25).BuildServiceProvider();

        Assert.Equal(25, provider.GetRequiredService<ConversationOptions>().MaxListLimit);
    }
}
