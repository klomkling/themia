using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.Hmac;
using Themia.Messaging.Http.DependencyInjection;

using Xunit;

namespace Themia.Messaging.Http.Tests;

// AddThemiaMessagingHttp depends on AddThemiaMessagingHmac having registered HmacOptions first:
// HttpMessageDispatcher resolves it at delivery time. Getting the order wrong used to fail only at
// first dispatch with an opaque "unable to resolve service for type HmacOptions" DI activation error;
// it must now fail loudly at registration time instead (mirrors AddThemiaMessagingInbox's prerequisite
// checks in Themia.Modules.Messaging).
public class HttpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddThemiaMessagingHttp_ShouldThrow_WhenAddThemiaMessagingHmacWasNotCalled()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaMessagingHttp());

        Assert.Contains("AddThemiaMessagingHmac", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaMessagingHttp_ShouldNotThrow_WhenHmacOptionsIsAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HmacOptions());

        var exception = Record.Exception(() => services.AddThemiaMessagingHttp());

        Assert.Null(exception);
    }
}
