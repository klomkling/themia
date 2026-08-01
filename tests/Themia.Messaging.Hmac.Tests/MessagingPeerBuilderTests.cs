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
    public void AddPeer_ShouldThrow_WhenRouteIsConfigured_ButBaseAddressIsNotSet()
    {
        var options = new HmacOptions();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("peer", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
                p.Route("ListingCreated", "/api/v1/listings");
            }));

        Assert.Contains("BaseAddress", ex.Message, StringComparison.Ordinal);
    }

    // F7 (final whole-branch review): Accept and Route used to do last-write-wins while the sibling
    // AddPeer already throws on a duplicate peer name. A duplicated key id or message type in
    // configuration silently discarded one — now they are consistent with AddPeer.
    [Fact]
    public void Accept_ShouldThrow_WhenKeyIdIsAlreadyRegistered()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HmacOptions().AddPeer("peer", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "first-secret");
                p.Accept("in-1", "second-secret");
            }));

        Assert.Contains("in-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_ShouldThrow_WhenMessageTypeIsAlreadyRegistered()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HmacOptions().AddPeer("peer", p =>
            {
                p.BaseAddress = new Uri("https://example.test");
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
                p.Route("ListingCreated", "/api/v1/listings");
                p.Route("ListingCreated", "/api/v2/listings");
            }));

        Assert.Contains("ListingCreated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPeer_ShouldBuild_WhenInboundOnlyPeerHasNoRoutesOrBaseAddress()
    {
        var options = new HmacOptions();

        options.AddPeer("inbound-peer", p =>
        {
            p.SignWith("out-1", "secret");
            p.Accept("in-1", "secret");
        });

        Assert.True(options.TryGetPeer("inbound-peer", out var peer));
        Assert.Null(peer!.BaseAddress);
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

        // F4 (final whole-branch review): the outbound secret is no longer a public property (it must
        // never be loggable or serialisable by an adopter) — SignOutbound proves it was captured
        // correctly without ever exposing it back out.
        var (keyId, signature) = peer.SignOutbound("canonical-string");
        Assert.Equal("out-1", keyId);
        Assert.Equal(ThemiaHmacV1.Sign("canonical-string", "out-secret"), signature);

        Assert.Equal("in-secret", peer.InboundKeys["in-1"]);
        Assert.Equal("/api/v1/listings", peer.Routes["ListingCreated"]);
    }

    [Fact]
    public void SignOutbound_ShouldReturnTheOutboundKeyIdAndMatchingSignature()
    {
        var options = new HmacOptions();
        options.AddPeer("peer", p =>
        {
            p.SignWith("out-1", "out-secret");
            p.Accept("in-1", "in-secret");
        });
        Assert.True(options.TryGetPeer("peer", out var peer));

        var (keyId, signature) = peer!.SignOutbound("some-canonical-string");

        Assert.Equal("out-1", keyId);
        Assert.Equal(ThemiaHmacV1.Sign("some-canonical-string", "out-secret"), signature);
    }
}
