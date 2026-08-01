using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Themia.Messaging.AspNetCore.DependencyInjection;
using Themia.Messaging.Hmac.DependencyInjection;

using Xunit;

namespace Themia.Messaging.AspNetCore.Tests;

public class AddThemiaMessagingVerificationTests
{
    [Fact]
    public async Task AddThemiaMessagingVerification_ShouldWarnAtStartup_ForABiDirectionalPeerWithNoOriginHeader()
    {
        var provider = new RecordingLoggerProvider();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddProvider(provider));
                services.AddThemiaMessagingHmac(o => o.AddPeer("propertiezy", p =>
                {
                    p.SignWith("out-1", "secret");
                    p.Accept("in-1", "secret");
                }));
                services.AddThemiaMessagingVerification(o => o.MarkBiDirectional("propertiezy", sendsOriginHeader: false));
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains(provider.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("propertiezy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddThemiaMessagingVerification_ShouldNotWarn_WhenBiDirectionalPeerSendsOriginHeader()
    {
        var provider = new RecordingLoggerProvider();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddProvider(provider));
                services.AddThemiaMessagingHmac(o => o.AddPeer("propertiezy", p =>
                {
                    p.SignWith("out-1", "secret");
                    p.Accept("in-1", "secret");
                }));
                // sendsOriginHeader defaults to true: this peer DOES send Origin, so no gap to warn about.
                services.AddThemiaMessagingVerification(o => o.MarkBiDirectional("propertiezy"));
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.DoesNotContain(provider.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AddThemiaMessagingVerification_ShouldNotWarn_WhenNoPeerIsDeclaredBiDirectional()
    {
        var provider = new RecordingLoggerProvider();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddProvider(provider));
                services.AddThemiaMessagingHmac(o => o.AddPeer("propertiezy", p =>
                {
                    p.SignWith("out-1", "secret");
                    p.Accept("in-1", "secret");
                }));
                services.AddThemiaMessagingVerification();
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.DoesNotContain(provider.Entries, e => e.Level == LogLevel.Warning);
    }
}
