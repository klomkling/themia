using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Themia.Messaging.AspNetCore.DependencyInjection;
using Themia.Messaging.Hmac;

using Xunit;

namespace Themia.Messaging.AspNetCore.Tests;

public class HmacVerificationFilterTests
{
    private const string PeerName = "peer";
    private const string Secret = "test-shared-secret";
    private const string WrongSecret = "a-different-secret";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);

    // StatusCodeHttpResult.ExecuteAsync touches HttpContext.RequestServices, so a bare DefaultHttpContext
    // needs a non-null provider even though nothing in these tests is actually resolved from it.
    private static readonly IServiceProvider EmptyServices = new ServiceCollection().AddLogging().BuildServiceProvider();

    // --- Table: valid request runs the endpoint -------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldRunEndpointAndReturn2xx_ForAValidRequest()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // --- Table: body over MaxBodyBytes -> 413, before hashing ------------------------------------

    // The signature below is deliberately WRONG. If the filter reached hashing before checking size,
    // this would come back 401 (SignatureMismatch), not 413 — so a 413 here proves size is checked,
    // and the body is rejected, before any hashing happens.
    [Fact]
    public async Task InvokeAsync_ShouldReject413_WhenDeclaredContentLengthExceedsMaxBodyBytes_BeforeHashing()
    {
        var (options, peer) = BuildPeer(maxBodyBytes: 5);
        var body = "{\"much-too-long-for-the-configured-limit\":true}";
        var headers = SignedHeaders(peer, Now, body, WrongSecret);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
    }

    // A chunked request has no Content-Length, so the declared-length check can't catch it; the
    // bufferLimit passed to EnableBuffering is the backstop.
    [Fact]
    public async Task InvokeAsync_ShouldReject413_ViaBufferLimitBackstop_WhenContentLengthIsNotDeclared()
    {
        var (options, peer) = BuildPeer(maxBodyBytes: 5);
        var body = new string('a', 200);
        var headers = SignedHeaders(peer, Now, body, Secret);
        // A plain MemoryStream is already seekable, so EnableBuffering would not need to wrap it and the
        // limit would never be exercised — a non-seekable stream mirrors what Kestrel actually hands the
        // filter for a chunked request with no declared length.
        var httpContext = BuildContext(peer, headers, body, declareContentLength: false, nonSeekableBody: true);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
    }

    // F10 (final whole-branch review): the filter used to catch (IOException) around the buffered read
    // and return 413 with no log at all — a mid-read connection reset would then report to the operator
    // as "payload too large" with no diagnostic trail, indistinguishable from a client that actually sent
    // too much data. A warning naming the peer, carrying the exception, is now logged either way.
    [Fact]
    public async Task InvokeAsync_ShouldLogAWarning_WhenTheBufferLimitBackstopTrips()
    {
        var (options, peer) = BuildPeer(maxBodyBytes: 5);
        var body = new string('a', 200);
        var headers = SignedHeaders(peer, Now, body, Secret);
        var httpContext = BuildContext(peer, headers, body, declareContentLength: false, nonSeekableBody: true);
        var logger = new RecordingLogger<HmacVerificationFilter>();

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, logger: logger), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains(peer.Name, StringComparison.Ordinal));
        // Still never leaks the body itself.
        foreach (var (_, message) in logger.Entries)
        {
            Assert.DoesNotContain(body, message, StringComparison.Ordinal);
        }
    }

    // --- Table: scheme -----------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldReject400_WhenSchemeHeaderIsPresentAndUnrecognised()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, scheme: "themia-hmac-v2");
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRunEndpoint_WhenSchemeHeaderIsAbsent()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, scheme: null);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // --- Table: timestamp ----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldReject401_WhenTimestampHeaderIsMissing()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret);
        headers.Remove(peer.HeaderNames.Timestamp);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReject401_WhenTimestampIsUnparseable()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret);
        headers[peer.HeaderNames.Timestamp] = "not-a-timestamp";
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReject408_WhenTimestampIsOutsideTheWindow_AndLogSkewAndTolerance()
    {
        var (options, peer) = BuildPeer(toleranceSeconds: 300);
        var body = "{}";
        var stale = Now.AddMinutes(20);
        var headers = SignedHeaders(peer, stale, body, Secret);
        var httpContext = BuildContext(peer, headers, body);
        var logger = new RecordingLogger<HmacVerificationFilter>();

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, logger: logger), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status408RequestTimeout, status);

        // An operator must be able to tell a clock problem from an attack in one line.
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("skew", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tolerance", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Table: key-id -----------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldReject401_WhenKeyIdIsPresentButUnknown()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, keyId: "no-such-key");
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    // --- Table: signature ----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldReject401_WhenSignatureDoesNotMatch()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, WrongSecret);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    // --- Table: legacy two-header requests ------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldRunEndpoint_WhenOnlyTimestampAndSignatureAreSent()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, keyId: null, scheme: null, origin: null);
        var httpContext = BuildContext(peer, headers, body);

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // --- Table: loop guard ---------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldReturn200WithoutRunningEndpoint_WhenOriginMatchesOwnOrigin()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, origin: "self");
        var httpContext = BuildContext(peer, headers, body);
        var verification = new VerificationOptions { Origin = "self" };

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, verification), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRunEndpoint_WhenOriginDiffersFromOwnOrigin()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, origin: "someone-else");
        var httpContext = BuildContext(peer, headers, body);
        var verification = new VerificationOptions { Origin = "self" };

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, verification), httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRunEndpoint_WhenOriginHeaderIsAbsent()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, Secret, origin: null);
        var httpContext = BuildContext(peer, headers, body);
        var verification = new VerificationOptions { Origin = "self" };

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, verification), httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // This is THE ordering test: an attacker who forges Origin to claim to be this service, but cannot
    // produce a valid signature, must still be rejected 401 — never answered 200. If the loop guard ran
    // BEFORE verification, this would wrongly short-circuit to 200 without ever checking the signature.
    [Fact]
    public async Task InvokeAsync_ShouldReject401_NotShortCircuitTo200_WhenOriginMatchesButSignatureIsInvalid()
    {
        var (options, peer) = BuildPeer();
        var body = "{}";
        var headers = SignedHeaders(peer, Now, body, WrongSecret, origin: "self");
        var httpContext = BuildContext(peer, headers, body);
        var verification = new VerificationOptions { Origin = "self" };

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options, verification), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    // --- Body rewind -------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldRewindTheBody_SoTheEndpointCanReadItAgain()
    {
        var (options, peer) = BuildPeer();
        var body = "{\"visitor\":\"a name with unicode: สวัสดี\"}";
        var headers = SignedHeaders(peer, Now, body, Secret);
        var httpContext = BuildContext(peer, headers, body);

        string? observedByEndpoint = null;
        var context = EndpointFilterInvocationContext.Create(httpContext);
        EndpointFilterDelegate next = async ctx =>
        {
            using var reader = new StreamReader(ctx.HttpContext.Request.Body, leaveOpen: true);
            observedByEndpoint = await reader.ReadToEndAsync();
            return Results.StatusCode(StatusCodes.Status200OK);
        };

        var result = await BuildFilter(options).InvokeAsync(context, next);
        if (result is IResult ir)
        {
            await ir.ExecuteAsync(httpContext);
        }

        Assert.Equal(body, observedByEndpoint);
    }

    // --- No secret or signature ever appears in a log message ------------------------------------

    [Fact]
    public async Task InvokeAsync_ShouldNeverLog_TheSecretOrTheSignature()
    {
        var (options, peer) = BuildPeer();
        var logger = new RecordingLogger<HmacVerificationFilter>();
        var filter = BuildFilter(options, logger: logger);

        // Exercise every rejection path that logs.
        var staleHeaders = SignedHeaders(peer, Now.AddMinutes(20), "{}", Secret);
        var staleSignature = staleHeaders[peer.HeaderNames.Signature];
        await InvokeFilterAsync(filter, BuildContext(peer, staleHeaders, "{}"));

        var mismatchHeaders = SignedHeaders(peer, Now, "{}", WrongSecret);
        var mismatchSignature = mismatchHeaders[peer.HeaderNames.Signature];
        await InvokeFilterAsync(filter, BuildContext(peer, mismatchHeaders, "{}"));

        foreach (var (_, message) in logger.Entries)
        {
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain(WrongSecret, message, StringComparison.Ordinal);
            Assert.DoesNotContain(staleSignature!, message, StringComparison.Ordinal);
            Assert.DoesNotContain(mismatchSignature!, message, StringComparison.Ordinal);
        }
    }

    // --- Unknown peer (defensive; not in the normative table, but reachable via a config mistake) ---

    [Fact]
    public async Task InvokeAsync_ShouldReject401_WhenNoPeerIsConfiguredForTheEndpointsMetadata()
    {
        var options = new HmacOptions(); // no peers registered at all
        var body = "{}";
        var httpContext = new DefaultHttpContext { RequestServices = EmptyServices };
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/ingest";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.ContentLength = body.Length;
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ThemiaHmacPeerMetadata("unregistered-peer")), "test"));

        var (status, nextCalled) = await InvokeFilterAsync(BuildFilter(options), httpContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    // --- Test helpers ------------------------------------------------------------------------------

    private static (HmacOptions Options, MessagingPeer Peer) BuildPeer(
        long maxBodyBytes = 4 * 1024 * 1024, int toleranceSeconds = 300, string prefix = "X-Themia-")
    {
        var options = new HmacOptions();
        options.AddPeer(PeerName, p =>
        {
            p.HeaderPrefix = prefix;
            p.ClockSkewTolerance = TimeSpan.FromSeconds(toleranceSeconds);
            p.MaxBodyBytes = maxBodyBytes;
            p.SignWith("out-1", Secret);
            p.Accept("in-1", Secret);
        });

        Assert.True(options.TryGetPeer(PeerName, out var peer));
        return (options, peer!);
    }

    private static Dictionary<string, string?> SignedHeaders(
        MessagingPeer peer, DateTimeOffset stamp, string body, string secret,
        string method = "POST", string path = "/ingest",
        string? keyId = "in-1", string? scheme = ThemiaHmacV1.SchemeName, string? origin = null)
    {
        var timestamp = ThemiaHmacV1.FormatTimestamp(stamp);
        var signature = ThemiaHmacV1.Sign(ThemiaHmacV1.Canonicalize(timestamp, method, path, body), secret);
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [peer.HeaderNames.Timestamp] = timestamp,
            [peer.HeaderNames.Signature] = signature,
        };
        if (keyId is not null) headers[peer.HeaderNames.KeyId] = keyId;
        if (scheme is not null) headers[peer.HeaderNames.Scheme] = scheme;
        if (origin is not null) headers[peer.HeaderNames.Origin] = origin;
        return headers;
    }

    private static DefaultHttpContext BuildContext(
        MessagingPeer peer, Dictionary<string, string?> headers, string body,
        string method = "POST", string path = "/ingest", bool declareContentLength = true, bool nonSeekableBody = false)
    {
        var httpContext = new DefaultHttpContext { RequestServices = EmptyServices };
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        Stream bodyStream = new MemoryStream(bodyBytes);
        if (nonSeekableBody)
        {
            bodyStream = new NonSeekableStream(bodyStream);
        }

        httpContext.Request.Body = bodyStream;
        if (declareContentLength)
        {
            httpContext.Request.ContentLength = bodyBytes.LongLength;
        }

        foreach (var (key, value) in headers)
        {
            if (value is not null)
            {
                httpContext.Request.Headers[key] = value;
            }
        }

        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ThemiaHmacPeerMetadata(peer.Name)), "test"));
        return httpContext;
    }

    private static HmacVerificationFilter BuildFilter(
        HmacOptions options, VerificationOptions? verification = null, TimeProvider? time = null,
        RecordingLogger<HmacVerificationFilter>? logger = null)
        => new(options, new HmacVerifier(), verification ?? new VerificationOptions(), time ?? new FakeTimeProvider(Now),
            logger ?? new RecordingLogger<HmacVerificationFilter>());

    private static async Task<(int Status, bool NextCalled)> InvokeFilterAsync(HmacVerificationFilter filter, DefaultHttpContext httpContext)
    {
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status200OK));
        };

        var context = EndpointFilterInvocationContext.Create(httpContext);
        var result = await filter.InvokeAsync(context, next);
        if (result is IResult ir)
        {
            await ir.ExecuteAsync(httpContext);
        }

        return (httpContext.Response.StatusCode, nextCalled);
    }

    // Forward-only, non-seekable read stream: mirrors what Kestrel hands a filter for a chunked request
    // (no Content-Length, no seeking) so EnableBuffering's bufferLimit backstop actually gets exercised
    // instead of being skipped because the body was already seekable.
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
