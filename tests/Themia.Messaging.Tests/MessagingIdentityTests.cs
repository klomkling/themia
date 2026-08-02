using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.DependencyInjection;

using Xunit;

namespace Themia.Messaging.Tests;

public class MessagingIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldThrow_WhenOriginIsBlank(string? origin)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MessagingIdentity(origin!));
    }

    [Fact]
    public void Constructor_ShouldExposeOrigin()
    {
        Assert.Equal("propertiezy", new MessagingIdentity("propertiezy").Origin);
    }

    [Fact]
    public void AddThemiaMessagingIdentity_ShouldRegisterTheIdentity()
    {
        var services = new ServiceCollection();

        services.AddThemiaMessagingIdentity("propertiezy");

        var identity = services.BuildServiceProvider().GetRequiredService<MessagingIdentity>();
        Assert.Equal("propertiezy", identity.Origin);
    }

    [Fact]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenCalledASecondTime()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingIdentity("propertiezy");

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaMessagingIdentity("ezy-assets"));

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }

    // The instance-scan alternative would miss this: a factory registration has a null
    // ImplementationInstance, so a second descriptor would be appended and DI would resolve the
    // LAST one — two identities coexisting with the later silently winning, which is the exact
    // drift this type exists to remove.
    [Fact]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenIdentityWasRegisteredViaAFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new MessagingIdentity("registered-directly"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaMessagingIdentity("propertiezy"));

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenOriginIsBlank(string? origin)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() => services.AddThemiaMessagingIdentity(origin!));
    }
}
