using Themia.TestSupport;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using Themia.Messaging.AspNetCore.DependencyInjection;
using Themia.Messaging.DependencyInjection;
using Themia.Messaging.Hmac;
using Themia.Messaging.Hmac.DependencyInjection;
using Themia.Messaging.Http;
using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.AspNetCore.Tests;

/// <summary>
/// The end-to-end round trip: a real <see cref="HttpMessageDispatcher"/> sending to a real
/// <see cref="HmacVerificationFilter"/> hosted on a <see cref="TestServer"/>, with nothing shared between
/// the two sides but the wire (each side builds its own <see cref="HmacOptions"/> instance). Every other
/// test in this solution proves one half against its own expectations; this is the only suite that proves
/// the dispatcher's signature is the one the filter accepts.
/// </summary>
/// <remarks>
/// Neither the signer nor the verifier is stubbed anywhere in this file: signing happens exclusively
/// inside <see cref="HttpMessageDispatcher"/> (which calls the real <c>ThemiaHmacV1</c> statics), and
/// verification happens exclusively inside <see cref="HmacVerificationFilter"/> (via the real
/// <c>HmacVerifier</c>, registered through <c>AddThemiaMessagingHmac</c>).
/// </remarks>
public class RoundTripTests
{
    private const string IngestRoute = "/ingest";
    private const string MessageType = "lead.created.v1";
    private const string KeyId = "hmac-key-1";
    private const string Secret = "round-trip-shared-secret";
    private const string SenderPeerNameOnReceiver = "sender-app"; // how the receiver's HmacOptions names this sender
    private const string ReceiverPeerNameOnDispatcher = "receiver-app"; // how the dispatcher's HmacOptions names this receiver

    // --- Case 1: the round trip the whole task exists for -----------------------------------------

    [Fact]
    public async Task DispatchedRow_IsVerifiedAndDelivered()
    {
        await using var receiver = await Receiver.StartAsync(SenderPeerNameOnReceiver, KeyId, Secret);
        using var dispatcher = Dispatcher.Create(ReceiverPeerNameOnDispatcher, receiver.Server, IngestRoute, MessageType, KeyId, Secret);

        var result = await dispatcher.DispatchAsync(BuildRow(destination: ReceiverPeerNameOnDispatcher));

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Equal(HttpStatusCode.OK, dispatcher.LastStatusCode);
        Assert.Equal(1, receiver.Tracker.Count);
    }

    // --- Case 2: the legacy header prefix round-trips ----------------------------------------------

    [Fact]
    public async Task LegacyHeaderPrefix_RoundTrips()
    {
        const string legacyPrefix = "X-Propertiezy-";

        await using var receiver = await Receiver.StartAsync(SenderPeerNameOnReceiver, KeyId, Secret, headerPrefix: legacyPrefix);
        using var dispatcher = Dispatcher.Create(
            ReceiverPeerNameOnDispatcher, receiver.Server, IngestRoute, MessageType, KeyId, Secret, headerPrefix: legacyPrefix);

        var result = await dispatcher.DispatchAsync(BuildRow(destination: ReceiverPeerNameOnDispatcher));

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Equal(HttpStatusCode.OK, dispatcher.LastStatusCode);
        Assert.Equal(1, receiver.Tracker.Count);
    }

    // --- Case 3: wrong secret => 401 => Permanent ---------------------------------------------------

    [Fact]
    public async Task WrongSecret_Returns401_AndClassifiesPermanent()
    {
        const string wrongSecret = "an-entirely-different-secret";

        await using var receiver = await Receiver.StartAsync(SenderPeerNameOnReceiver, KeyId, Secret);
        using var dispatcher = Dispatcher.Create(
            ReceiverPeerNameOnDispatcher, receiver.Server, IngestRoute, MessageType, KeyId, wrongSecret);

        var result = await dispatcher.DispatchAsync(BuildRow(destination: ReceiverPeerNameOnDispatcher));

        Assert.Equal(HttpStatusCode.Unauthorized, dispatcher.LastStatusCode);
        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Equal(0, receiver.Tracker.Count);
    }

    // --- Case 4: clock skew => 408 => Transient -----------------------------------------------------

    // HttpMessageDispatcher has no TimeProvider seam — it signs with DateTimeOffset.UtcNow directly
    // (see Themia.Messaging.Http/HttpMessageDispatcher.cs, Sign()), unlike HmacVerificationFilter, which
    // does take one. See the report for this finding. The verifier's staleness check is
    // `(now - sentAt).Duration() > tolerance` (HmacVerifier.Verify), which is symmetric: moving the
    // RECEIVER's clock 10 minutes away from a correctly-signed, real "now" timestamp produces the
    // identical 10-minute skew, the identical 408, and the identical wire bytes as a sender whose clock
    // runs 10 minutes fast against a receiver on the correct time. The dispatcher's own signing path is
    // exercised unmodified and for real; only the receiver's clock is controlled, via the seam that
    // actually exists.
    [Fact]
    public async Task ClockSkewTenMinutes_Returns408_AndClassifiesTransient()
    {
        var skewedReceiverClock = new FakeTimeProvider(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10));

        await using var receiver = await Receiver.StartAsync(
            SenderPeerNameOnReceiver, KeyId, Secret, timeProvider: skewedReceiverClock);
        using var dispatcher = Dispatcher.Create(ReceiverPeerNameOnDispatcher, receiver.Server, IngestRoute, MessageType, KeyId, Secret);

        var result = await dispatcher.DispatchAsync(BuildRow(destination: ReceiverPeerNameOnDispatcher));

        Assert.Equal(HttpStatusCode.RequestTimeout, dispatcher.LastStatusCode);
        Assert.Equal(DispatchOutcome.Transient, result.Outcome);
        Assert.Equal(0, receiver.Tracker.Count);
    }

    // --- Case 5: loop guard — matching Origin answers 200 without the endpoint running --------------

    [Fact]
    public async Task MatchingOrigin_Returns200WithoutRunningTheEndpoint_AndDispatcherRecordsDelivered()
    {
        const string ownOrigin = "receiver-service";

        await using var receiver = await Receiver.StartAsync(SenderPeerNameOnReceiver, KeyId, Secret, ownOrigin: ownOrigin);
        using var dispatcher = Dispatcher.Create(ReceiverPeerNameOnDispatcher, receiver.Server, IngestRoute, MessageType, KeyId, Secret);

        var row = BuildRow(destination: ReceiverPeerNameOnDispatcher, origin: ownOrigin);
        var result = await dispatcher.DispatchAsync(row);

        Assert.Equal(HttpStatusCode.OK, dispatcher.LastStatusCode);
        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Equal(0, receiver.Tracker.Count); // the endpoint never ran
    }

    private static ClaimedMessageRow BuildRow(string destination, string origin = "sender-app-origin")
        => new(
            Id: Guid.NewGuid(),
            MessageId: Guid.NewGuid(),
            TenantId: null,
            Type: MessageType,
            Payload: "{\"leadId\":\"11111111-1111-1111-1111-111111111111\"}",
            Destination: destination,
            Origin: origin,
            EntityKey: null,
            Version: null,
            Headers: null,
            Attempts: 0);

    /// <summary>The receiving side: its own <see cref="HmacOptions"/>, hosted on a real <see cref="TestServer"/>.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly IHost host;

        private Receiver(IHost host, TestServer server, InvocationTracker tracker)
        {
            this.host = host;
            Server = server;
            Tracker = tracker;
        }

        public TestServer Server { get; }

        public InvocationTracker Tracker { get; }

        public static async Task<Receiver> StartAsync(
            string requirePeerName,
            string inboundKeyId,
            string inboundSecret,
            string headerPrefix = HmacHeaderNames.DefaultPrefix,
            string? ownOrigin = null,
            TimeProvider? timeProvider = null)
        {
            var tracker = new InvocationTracker();

            var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        if (timeProvider is not null)
                        {
                            // Must be registered before AddThemiaMessagingVerification: that call only
                            // TryAddSingleton(TimeProvider.System), so an earlier registration wins.
                            services.AddSingleton(timeProvider);
                        }

                        services.AddThemiaMessagingHmac(o => o.AddPeer(requirePeerName, p =>
                        {
                            p.HeaderPrefix = headerPrefix;
                            // Required by MessagingPeerBuilder.Build even though this peer is inbound-only
                            // in this test — never used to sign anything here.
                            p.SignWith("receiver-unused-out-key", "receiver-unused-out-secret");
                            p.Accept(inboundKeyId, inboundSecret);
                        }));
                        services.AddThemiaMessagingIdentity(ownOrigin ?? "receiver-default-origin");
                        services.AddThemiaMessagingVerification();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost(IngestRoute, (HttpContext _) =>
                            {
                                tracker.Increment();
                                return Results.Ok();
                            }).RequireThemiaHmac(requirePeerName);
                        });
                    }))
                .StartAsync();

            return new Receiver(host, host.GetTestServer(), tracker);
        }

        public ValueTask DisposeAsync()
        {
            host.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The sending side: its own <see cref="HmacOptions"/>, delivering through the receiver's <see cref="TestServer"/>.</summary>
    private sealed class Dispatcher : IDisposable
    {
        private readonly HttpMessageDispatcher inner;
        private readonly StatusRecordingHandler handler;

        private Dispatcher(HttpMessageDispatcher inner, StatusRecordingHandler handler)
        {
            this.inner = inner;
            this.handler = handler;
        }

        public HttpStatusCode? LastStatusCode => handler.LastStatusCode;

        public static Dispatcher Create(
            string destinationPeerName,
            TestServer server,
            string route,
            string messageType,
            string outboundKeyId,
            string outboundSecret,
            string headerPrefix = HmacHeaderNames.DefaultPrefix)
        {
            var options = new HmacOptions();
            options.AddPeer(destinationPeerName, p =>
            {
                p.HeaderPrefix = headerPrefix;
                p.BaseAddress = server.BaseAddress;
                p.SignWith(outboundKeyId, outboundSecret);
                // Required by MessagingPeerBuilder.Build even though nothing inbound reaches this side
                // in this test — the dispatcher never verifies anything.
                p.Accept("dispatcher-unused-in-key", "dispatcher-unused-in-secret");
                p.Route(messageType, route);
            });

            var handler = new StatusRecordingHandler(server.CreateHandler());
            var factory = new SingleHandlerHttpClientFactory(handler);
            var dispatcher = new HttpMessageDispatcher(factory, options, new RecordingLogger<HttpMessageDispatcher>());

            return new Dispatcher(dispatcher, handler);
        }

        public Task<DispatchResult> DispatchAsync(ClaimedMessageRow row)
            => inner.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        public void Dispose() => handler.Dispose();
    }

    /// <summary>Counts endpoint invocations without any shared mutable static state between tests.</summary>
    private sealed class InvocationTracker
    {
        private int count;

        public int Count => count;

        public void Increment() => Interlocked.Increment(ref count);
    }

    /// <summary>Always hands back an <see cref="HttpClient"/> wrapping the one handler under test, regardless of the requested name.</summary>
    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Observes the real HTTP status the receiver answered with, without altering the response in any way.</summary>
    private sealed class StatusRecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public HttpStatusCode? LastStatusCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            LastStatusCode = response.StatusCode;
            return response;
        }
    }

    /// <summary>An <see cref="IServiceProvider"/> with nothing registered, for a dispatcher that needs no scoped services in tests.</summary>
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
