using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.DependencyInjection;

using Xunit;

namespace Themia.Messaging.Hmac.Tests;

public class MessagingIdentityTests
{
    // HTTP strips optional whitespace around a header value in transit (RFC 9110 5.5), so an origin with
    // stray padding would be stamped padded and arrive trimmed — and LoopGuard's Ordinal comparison would
    // then never match, silently disabling loop protection. Trimming at construction is what stops that.
    [Theory]
    [InlineData("  svc-a", "svc-a")]
    [InlineData("svc-a  ", "svc-a")]
    [InlineData("\tsvc-a\n", "svc-a")]
    public void Constructor_ShouldTrimSurroundingWhitespace(string input, string expected)
    {
        Assert.Equal(expected, new MessagingIdentity(input).Origin);
    }

    // The origin column is varchar(100) in both the outbox and inbox schemas. Without this check an
    // over-long origin fails at the first publish (Postgres/SQL Server) or is silently truncated into a
    // permanently non-matching origin (MySQL, non-strict).
    [Fact]
    public void Constructor_ShouldThrow_WhenOriginExceedsTheColumnWidth()
    {
        var tooLong = new string('x', MessagingIdentity.MaxOriginLength + 1);

        var ex = Assert.Throws<ArgumentException>(() => new MessagingIdentity(tooLong));

        Assert.Equal("origin", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldAccept_AnOriginExactlyAtTheColumnWidth()
    {
        var exact = new string('x', MessagingIdentity.MaxOriginLength);

        Assert.Equal(exact, new MessagingIdentity(exact).Origin);
    }

    // Length is measured AFTER trimming: padding is not part of what reaches the column.
    [Fact]
    public void Constructor_ShouldMeasureLengthAfterTrimming()
    {
        var padded = "  " + new string('x', MessagingIdentity.MaxOriginLength) + "  ";

        Assert.Equal(MessagingIdentity.MaxOriginLength, new MessagingIdentity(padded).Origin.Length);
    }

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
