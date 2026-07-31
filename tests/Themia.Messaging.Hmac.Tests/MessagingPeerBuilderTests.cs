using Xunit;

namespace Themia.Messaging.Hmac.Tests;

public class MessagingPeerBuilderTests
{
    [Fact]
    public void AddPeer_ShouldThrow_WhenNameIsBlank()
    {
        var options = new HmacOptions();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("   ", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
            }));

        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddPeer_ShouldThrow_WhenNoOutboundKeyIsSet()
    {
        var options = new HmacOptions();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("peer", p => p.Accept("in-1", "secret")));

        Assert.Contains("SignWith", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPeer_ShouldThrow_WhenNoInboundKeyIsAccepted()
    {
        var options = new HmacOptions();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("peer", p => p.SignWith("out-1", "secret")));

        Assert.Contains("Accept", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPeer_ShouldThrow_WhenClockSkewToleranceIsNotPositive(int seconds)
    {
        var options = new HmacOptions();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("peer", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
                p.ClockSkewTolerance = TimeSpan.FromSeconds(seconds);
            }));

        Assert.Contains("ClockSkewTolerance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPeer_ShouldBuildAndExposeConfiguredValues_WhenValid()
    {
        var options = new HmacOptions();

        options.AddPeer("peer", p =>
        {
            p.HeaderPrefix = "X-Custom-";
            p.BaseAddress = new Uri("https://example.test");
            p.ClockSkewTolerance = TimeSpan.FromMinutes(10);
            p.MaxBodyBytes = 1024;
            p.SignWith("out-1", "out-secret");
            p.Accept("in-1", "in-secret");
            p.Route("ListingCreated", "/api/v1/listings");
        });

        Assert.True(options.TryGetPeer("peer", out var peer));
        Assert.Equal("peer", peer!.Name);
        Assert.Equal("X-Custom-", peer.HeaderPrefix);
        Assert.Equal(new Uri("https://example.test"), peer.BaseAddress);
        Assert.Equal(TimeSpan.FromMinutes(10), peer.ClockSkewTolerance);
        Assert.Equal(1024, peer.MaxBodyBytes);
        Assert.Equal("out-1", peer.OutboundKeyId);
        Assert.Equal("out-secret", peer.OutboundSecret);
        Assert.Equal("in-secret", peer.InboundKeys["in-1"]);
        Assert.Equal("/api/v1/listings", peer.Routes["ListingCreated"]);
    }
}
