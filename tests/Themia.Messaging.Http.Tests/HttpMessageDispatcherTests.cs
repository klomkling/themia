using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Themia.Messaging.Hmac;
using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Http.Tests;

public class HttpMessageDispatcherTests
{
    private const string PeerName = "propertiezy";
    private const string OutboundKeyId = "themia-out-1";
    private const string OutboundSecret = "test-outbound-secret-please-change";
    private const string MessageType = "lead.created.v1";
    private const string RoutePath = "/api/v1/leads";
    private static readonly Uri BaseAddress = new("https://peer.example.test");

    [Fact]
    public async Task DispatchAsync_ShouldSignWithOutboundKey_AndSendSignatureHeadersUnderPeerPrefix()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.True(handler.WasCalled);

        var names = new HmacHeaderNames(HmacHeaderNames.DefaultPrefix);
        var request = handler.LastRequest!;
        Assert.True(request.Headers.TryGetValues(names.Timestamp, out var timestamps));
        Assert.True(request.Headers.TryGetValues(names.Signature, out var signatures));
        Assert.True(request.Headers.TryGetValues(names.KeyId, out var keyIds));
        Assert.True(request.Headers.TryGetValues(names.Scheme, out var schemes));
        Assert.True(request.Headers.TryGetValues(names.Origin, out var origins));

        Assert.Equal(OutboundKeyId, keyIds!.Single());
        Assert.Equal(ThemiaHmacV1.SchemeName, schemes!.Single());
        Assert.Equal(row.Origin, origins!.Single());
        Assert.True(ThemiaHmacV1.TryParseTimestamp(timestamps!.Single(), out _));

        // The signature must verify against the canonical string built from what was ACTUALLY sent
        // (pathAndQuery + body), not a re-derived guess — this is what makes constraint #3 provable.
        var canonical = ThemiaHmacV1.Canonicalize(
            timestamps!.Single(), "POST", request.RequestUri!.PathAndQuery, handler.LastRequestBody!);
        var expectedSignature = ThemiaHmacV1.Sign(canonical, OutboundSecret);
        Assert.Equal(expectedSignature, signatures!.Single());
    }

    [Fact]
    public async Task DispatchAsync_ShouldSendPayloadVerbatim()
    {
        const string payload = "{\"name\":\"สมชาย ใจดี\",\"message\":\"บรรทัดแรก\nบรรทัดที่สอง\"}";
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow(payload: payload);

        await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        // Byte-for-byte, not just string equality via a re-encoding path: a pretty-print, a JsonContent
        // re-encode, or a different charset would still often look "equal" as a .NET string but change
        // what actually goes on the wire and over the signature.
        Assert.Equal(Encoding.UTF8.GetBytes(payload), handler.LastRequestBodyBytes);
        Assert.Equal(payload, handler.LastRequestBody);
    }

    [Fact]
    public async Task DispatchAsync_ShouldResolveUrl_FromBaseAddressAndConfiguredRoute()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();

        await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(new Uri(BaseAddress, RoutePath), handler.LastRequest!.RequestUri);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnPermanent_WhenTypeIsUnrouted_WithoutSending()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow(type: "no.such.route.v1");

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnPermanent_WhenDestinationIsUnknown_WithoutSending()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow(destination: "no-such-peer");

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnPermanent_WhenPeerHasNoBaseAddress_WithoutSending()
    {
        // MessagingPeerBuilder.Build now refuses to construct a peer that has routes but no
        // BaseAddress (see Themia.Messaging.Hmac/MessagingPeer.cs), so this state can no longer arise
        // through the public API — the dispatcher's guard below is defensive, not reachable, code.
        // This test reaches it anyway, via reflection into the internal MessagingPeer constructor and
        // HmacOptions' peer registry, so the guard is proven correct rather than merely present.
        var options = new HmacOptions();
        var peer = CreatePeerWithoutBaseAddress();
        InjectPeer(options, peer);
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow(destination: peer.Name);

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.False(handler.WasCalled);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, DispatchOutcome.Delivered)]
    [InlineData(HttpStatusCode.RequestTimeout, DispatchOutcome.Transient)] // 408 — the scheme's stale-timestamp status
    [InlineData(HttpStatusCode.Unauthorized, DispatchOutcome.Permanent)]
    public async Task DispatchAsync_ShouldClassifyResponse_ViaHttpStatusClassifier(HttpStatusCode status, DispatchOutcome expected)
    {
        var options = BuildOptions();
        var handler = new StubHandler(status);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnTransient_WhenHttpRequestExceptionIsThrown()
    {
        var options = BuildOptions();
        var thrown = new HttpRequestException("connection refused");
        var handler = new StubHandler(thrown);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Transient, result.Outcome);
        Assert.Same(thrown, result.Exception);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnTransient_WhenTimeoutSurfacesAsTaskCanceledException()
    {
        var options = BuildOptions();
        // Simulates HttpClient.Timeout firing: a TaskCanceledException tied to an internal token, while
        // the CALLER's token (CancellationToken.None below) is never touched.
        var handler = new StubHandler(new TaskCanceledException("The request timed out.", new TimeoutException()));
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task DispatchAsync_ShouldPropagate_WhenCallersTokenIsCancelled()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Host shutdown must burn no attempt: the exception propagates rather than becoming a
        // Transient/Permanent DispatchResult the drainer would record.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(new NullServiceProvider(), row, cts.Token));
    }

    [Fact]
    public async Task DispatchAsync_ShouldMergeEnvelopeHeaders_WithoutOverwritingSignatureHeaders()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var names = new HmacHeaderNames(HmacHeaderNames.DefaultPrefix);
        var envelopeHeaders = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-Custom-Trace"] = "trace-abc-123",
            [names.Signature] = "forged-signature-attempt",
        });
        var row = BuildRow(headers: envelopeHeaders);

        await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        var request = handler.LastRequest!;
        Assert.True(request.Headers.TryGetValues("X-Custom-Trace", out var trace));
        Assert.Equal("trace-abc-123", trace!.Single());

        Assert.True(request.Headers.TryGetValues(names.Timestamp, out var timestamps));
        Assert.True(request.Headers.TryGetValues(names.Signature, out var signatures));
        Assert.Single(signatures!); // envelope attempt did not add a second value or replace the real one

        var canonical = ThemiaHmacV1.Canonicalize(
            timestamps!.Single(), "POST", request.RequestUri!.PathAndQuery, handler.LastRequestBody!);
        var expectedSignature = ThemiaHmacV1.Sign(canonical, OutboundSecret);
        Assert.Equal(expectedSignature, signatures!.Single());
        Assert.NotEqual("forged-signature-attempt", signatures!.Single());
    }

    [Fact]
    public async Task DispatchAsync_ShouldStillDeliver_WhenEnvelopeHeadersJsonIsMalformed()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.OK);
        var dispatcher = BuildDispatcher(options, handler);
        var row = BuildRow(headers: "{not valid json");

        var result = await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.True(handler.WasCalled);
    }

    [Fact]
    public async Task DispatchAsync_ShouldNeverLogTheSecretOrTheSignature()
    {
        var options = BuildOptions();
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var logger = new RecordingLogger<HttpMessageDispatcher>();
        var dispatcher = new HttpMessageDispatcher(new StubHttpClientFactory(handler), options, logger);
        var row = BuildRow();

        await dispatcher.DispatchAsync(new NullServiceProvider(), row, CancellationToken.None);

        var expectedSignature = ThemiaHmacV1.Sign(
            ThemiaHmacV1.Canonicalize(
                handler.LastRequest!.Headers.GetValues(new HmacHeaderNames(HmacHeaderNames.DefaultPrefix).Timestamp).Single(),
                "POST",
                handler.LastRequest!.RequestUri!.PathAndQuery,
                handler.LastRequestBody!),
            OutboundSecret);

        Assert.DoesNotContain(logger.Messages, m => m.Contains(OutboundSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(expectedSignature, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(row.Payload, StringComparison.Ordinal));
    }

    private static HmacOptions BuildOptions()
    {
        var options = new HmacOptions();
        options.AddPeer(PeerName, p =>
        {
            p.BaseAddress = BaseAddress;
            p.SignWith(OutboundKeyId, OutboundSecret);
            p.Accept("in-1", "in-secret");
            p.Route(MessageType, RoutePath);
        });
        return options;
    }

    private static HttpMessageDispatcher BuildDispatcher(HmacOptions options, StubHandler handler)
        => new(new StubHttpClientFactory(handler), options, new RecordingLogger<HttpMessageDispatcher>());

    private static ClaimedMessageRow BuildRow(
        string? payload = null,
        string destination = PeerName,
        string type = MessageType,
        string origin = "propertiezy-app",
        string? headers = null)
        => new(
            Id: Guid.NewGuid(),
            MessageId: Guid.NewGuid(),
            TenantId: null,
            Type: type,
            Payload: payload ?? "{\"leadId\":\"11111111-1111-1111-1111-111111111111\"}",
            Destination: destination,
            Origin: origin,
            EntityKey: null,
            Version: null,
            Headers: headers,
            Attempts: 0);

    // MessagingPeerBuilder.Build validates that a peer with routes has a BaseAddress, so this
    // otherwise-invalid state can no longer be reached through HmacOptions.AddPeer. Bypasses that
    // builder via reflection into MessagingPeer's internal constructor, purely to exercise the
    // dispatcher's own defensive check against the same invariant.
    private static MessagingPeer CreatePeerWithoutBaseAddress()
    {
        var ctor = typeof(MessagingPeer).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types:
            [
                typeof(string), typeof(string), typeof(Uri), typeof(TimeSpan), typeof(long),
                typeof(string), typeof(string),
                typeof(IReadOnlyDictionary<string, string>), typeof(IReadOnlyDictionary<string, string>),
            ],
            modifiers: null)
            ?? throw new InvalidOperationException("MessagingPeer's internal constructor shape changed; update this test helper.");

        return (MessagingPeer)ctor.Invoke(
        [
            "no-base-address-peer",
            HmacHeaderNames.DefaultPrefix,
            null, // BaseAddress — the invalid state Build() now rejects
            TimeSpan.FromMinutes(5),
            4L * 1024 * 1024,
            OutboundKeyId,
            OutboundSecret,
            new Dictionary<string, string> { ["in-1"] = "in-secret" },
            new Dictionary<string, string> { [MessageType] = RoutePath },
        ]);
    }

    // HmacOptions only exposes peers through AddPeer (which runs the builder's validation), so
    // reaching the dispatcher's defensive branch also requires bypassing that registry directly.
    private static void InjectPeer(HmacOptions options, MessagingPeer peer)
    {
        var field = typeof(HmacOptions).GetField("_peers", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HmacOptions' internal peer field changed; update this test helper.");
        var peers = (Dictionary<string, MessagingPeer>)field.GetValue(options)!;
        peers[peer.Name] = peer;
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that always hands back a client wrapping <paramref name="handler"/>.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Captures the outgoing request/body and either returns a configured status or throws.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode? status;
    private readonly Exception? exceptionToThrow;

    public StubHandler(HttpStatusCode status) => this.status = status;

    public StubHandler(Exception exceptionToThrow) => this.exceptionToThrow = exceptionToThrow;

    public bool WasCalled { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public byte[]? LastRequestBodyBytes { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        LastRequest = request;

        if (request.Content is not null)
        {
            LastRequestBodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            LastRequestBody = Encoding.UTF8.GetString(LastRequestBodyBytes);
        }

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }

        return new HttpResponseMessage(status!.Value);
    }
}

/// <summary>An <see cref="ILogger{TCategoryName}"/> that records every formatted message for assertion.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}

/// <summary>An <see cref="IServiceProvider"/> with nothing registered, for dispatchers that don't need scoped services in tests.</summary>
internal sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
