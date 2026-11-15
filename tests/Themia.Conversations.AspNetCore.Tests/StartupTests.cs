using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Themia.Conversations.AspNetCore;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class StartupTests
{
    [Fact]
    public void MapThemiaConversations_ShouldThrow_WhenNoActorResolverIsRegistered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThemiaConversationsHttp();
        using var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() => app.MapThemiaConversations("/api/v1"));

        Assert.Contains(nameof(IConversationActorResolver), error.Message);
    }

    [Fact]
    public void MapThemiaConversations_ShouldNotThrow_WhenAnActorResolverIsRegistered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThemiaConversationsHttp();
        builder.Services.AddSingleton<IConversationActorResolver, NullResolver>();
        using var app = builder.Build();

        app.MapThemiaConversations("/api/v1");
    }

    private sealed class NullResolver : IConversationActorResolver
    {
        public ValueTask<ConversationCaller?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ConversationCaller?>(null);
    }
}
