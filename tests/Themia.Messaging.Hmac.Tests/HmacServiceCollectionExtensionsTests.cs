using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.Hmac.DependencyInjection;

using Xunit;

namespace Themia.Messaging.Hmac.Tests;

// F1 (final whole-branch review): a second AddThemiaMessagingHmac call used to build a complete second
// HmacOptions and then have TryAddSingleton throw it away silently, because the type was already
// registered — the same failure AddPeer was fixed to prevent (HmacOptionsTests), reintroduced one level
// up. In a modular host where two modules each register a peer, the second module's peers vanished and
// surfaced later as TryGetPeer misses: 401 on receive, Permanent dead-letter on send.
public class HmacServiceCollectionExtensionsTests
{
    [Fact]
    public void AddThemiaMessagingHmac_ShouldThrow_WhenCalledASecondTime()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingHmac(o => o.AddPeer("peer-1", p =>
        {
            p.SignWith("out-1", "secret");
            p.Accept("in-1", "secret");
        }));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddThemiaMessagingHmac(o => o.AddPeer("peer-2", p =>
            {
                p.SignWith("out-2", "secret");
                p.Accept("in-2", "secret");
            })));

        Assert.Contains("AddThemiaMessagingHmac", ex.Message, StringComparison.Ordinal);
        Assert.Contains("single", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddThemiaMessagingHmac_ShouldThrow_WhenHmacOptionsWasAlreadyRegisteredDirectly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HmacOptions());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddThemiaMessagingHmac(o => o.AddPeer("peer", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
            })));

        Assert.Contains("HmacOptions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaMessagingHmac_ShouldRegisterAllPeers_WhenCalledOnceWithMultiplePeers()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingHmac(o =>
        {
            o.AddPeer("peer-1", p =>
            {
                p.SignWith("out-1", "secret");
                p.Accept("in-1", "secret");
            });
            o.AddPeer("peer-2", p =>
            {
                p.SignWith("out-2", "secret");
                p.Accept("in-2", "secret");
            });
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<HmacOptions>();

        Assert.True(options.TryGetPeer("peer-1", out _));
        Assert.True(options.TryGetPeer("peer-2", out _));
    }
}
