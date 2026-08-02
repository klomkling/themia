using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.Hmac.DependencyInjection;
using Themia.Messaging.Http.DependencyInjection;
using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Http.Tests;

// F3 (final whole-branch review): AddHttpClient() alone leaves .NET's default AllowAutoRedirect = true
// on every per-peer client. A 301/302/303 then silently turns the signed POST into a GET and drops the
// body (the receiver 401s and the channel dead-letters looking like a key problem); a 307/308 replays the
// signed payload — a visitor's name and phone number on the lead channel — verbatim, with a valid
// signature, to whatever host Location names. HttpStatusClassifier already treats 3xx as Permanent; it
// just never used to see the 3xx because HttpClient followed it first. This suite proves the real DI
// registration (AddThemiaMessagingHmac + AddThemiaMessagingHttp), not a hand-built dispatcher, refuses to
// follow the redirect.
//
// A first version of this fix used ConfigureHttpClientDefaults, which applies to EVERY HttpClient the
// factory produces for the whole host — not just messaging peer clients. That would have silently broken
// OIDC redirect-following in Themia.Modules.Identity.ExternalAuth.AspNetCore (and any other
// IHttpClientFactory consumer never opted into this module) the moment a host registered both. The fix is
// now scoped per peer name, and NonPeerClient_ShouldStillFollowRedirects... below is the regression test
// that would have caught the broader version.
public class PeerHttpClientRedirectTests
{
    private const string PeerName = "peer";
    private const string MessageType = "lead.created.v1";
    private const string RoutePath = "/ingest";

    [Fact]
    public async Task DispatchAsync_ShouldNotFollowA302_AndShouldClassifyPermanent()
    {
        using var listener = new RedirectingListener(RoutePath);
        listener.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaMessagingHmac(o => o.AddPeer(PeerName, p =>
        {
            p.BaseAddress = listener.BaseAddress;
            p.SignWith("out-1", "secret");
            p.Accept("in-1", "secret");
            p.Route(MessageType, RoutePath);
        }));
        services.AddThemiaMessagingHttp();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IOutboxDispatcher<ClaimedMessageRow>>();

        var row = new ClaimedMessageRow(
            Id: Guid.NewGuid(),
            MessageId: Guid.NewGuid(),
            TenantId: null,
            Type: MessageType,
            Payload: "{\"leadId\":\"11111111-1111-1111-1111-111111111111\"}",
            Destination: PeerName,
            Origin: "sender-app",
            EntityKey: null,
            Version: null,
            Headers: null,
            Attempts: 0);

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        // A follow would have produced a second request (a GET to the redirect target, which the
        // listener answers 200 OK) — asserting exactly one request proves the redirect was never chased,
        // not merely that the eventual outcome happens to match.
        Assert.Equal(1, listener.RequestCount);
    }

    // The regression test for the coordinator-flagged issue: a module that never opted into messaging's
    // "peer clients refuse redirects" rule (e.g. Themia.Modules.Identity.ExternalAuth.AspNetCore's OIDC
    // client, which DEPENDS on following redirects for discovery/authorization) must be completely
    // unaffected by AddThemiaMessagingHttp being registered in the same host. Without this test, nothing
    // stops a future change from reinstating ConfigureHttpClientDefaults (or an equivalent host-wide
    // default) and silently reintroducing the auth outage this fix exists to prevent.
    [Fact]
    public async Task NonPeerClient_ShouldStillFollowRedirects_WhenRegisteredAlongsideMessagingHttp()
    {
        using var listener = new RedirectingListener(RoutePath);
        listener.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaMessagingHmac(o => o.AddPeer(PeerName, p =>
        {
            p.BaseAddress = listener.BaseAddress;
            p.SignWith("out-1", "secret");
            p.Accept("in-1", "secret");
            p.Route(MessageType, RoutePath);
        }));
        services.AddThemiaMessagingHttp();
        // Stands in for a module (e.g. OIDC external auth) that registers its own named client and never
        // asked for messaging's redirect-refusal behaviour.
        services.AddHttpClient("some-other-client");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var otherClient = factory.CreateClient("some-other-client");

        using var response = await otherClient.GetAsync(new Uri(listener.BaseAddress, RoutePath));

        // The redirect WAS followed: the client landed on 200 from the redirect target, and the listener
        // saw two requests (the original GET to /ingest, then the followed GET to /redirected).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, listener.RequestCount);
    }

    /// <summary>A minimal loopback HTTP server that always answers the configured route with a 302 to a second path that would succeed if followed.</summary>
    private sealed class RedirectingListener : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly string redirectFromPath;
        private readonly string redirectToPath = "/redirected";
        private Task? acceptLoop;
        private int requestCount;

        public RedirectingListener(string redirectFromPath)
        {
            this.redirectFromPath = redirectFromPath;
            var port = GetFreeLoopbackPort();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(BaseAddress.ToString());
        }

        public Uri BaseAddress { get; }

        public int RequestCount => requestCount;

        public void Start()
        {
            listener.Start();
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener stopped/disposed
                }

                Interlocked.Increment(ref requestCount);

                if (context.Request.Url!.AbsolutePath == redirectFromPath)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Redirect;
                    context.Response.Headers["Location"] = new Uri(BaseAddress, redirectToPath).ToString();
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                }

                context.Response.Close();
            }
        }

        private static int GetFreeLoopbackPort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
            try
            {
                acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Best-effort shutdown; the loop's own catch already returns on listener disposal.
            }
        }
    }
}
