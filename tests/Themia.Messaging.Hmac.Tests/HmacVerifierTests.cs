using Xunit;

namespace Themia.Messaging.Hmac.Tests;

public class HmacVerifierTests
{
    private const string Secret = "test-shared-secret";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);

    private static MessagingPeer Peer(
        string prefix = "X-Themia-",
        int toleranceSeconds = 300,
        params (string KeyId, string Secret)[] inbound)
    {
        var options = new HmacOptions();
        options.AddPeer("peer", p =>
        {
            p.HeaderPrefix = prefix;
            p.ClockSkewTolerance = TimeSpan.FromSeconds(toleranceSeconds);
            p.SignWith("out-1", Secret);
            foreach (var (keyId, secret) in inbound.DefaultIfEmpty(("in-1", Secret)))
            {
                p.Accept(keyId, secret);
            }
        });

        Assert.True(options.TryGetPeer("peer", out var peer));
        return peer!;
    }

    private static Dictionary<string, string?> Headers(
        MessagingPeer peer, DateTimeOffset stamp, string body, string secret,
        string? keyId = "in-1", string? scheme = ThemiaHmacV1.SchemeName, string? origin = null,
        string method = "PUT", string path = "/api/v1/ingest/listings")
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

    private static HmacVerificationResult Verify(
        MessagingPeer peer, Dictionary<string, string?> headers, string body = "{}",
        string method = "PUT", string path = "/api/v1/ingest/listings", DateTimeOffset? now = null)
        => new HmacVerifier().Verify(peer, headers, method, path, body, now ?? Now);

    [Fact]
    public void Verify_ShouldSucceed_ForAWellFormedRequest()
    {
        var peer = Peer();

        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret)).Verdict);
    }

    // The live link sends ONLY timestamp and signature — no key-id, no scheme, no origin. Requiring any of
    // them would reject the entire existing integration on its first request.
    [Fact]
    public void Verify_ShouldSucceed_WithOnlyTimestampAndSignature()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret, keyId: null, scheme: null, origin: null);

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldTryEveryInboundKey_WhenKeyIdIsAbsent()
    {
        var peer = Peer(inbound: [("in-1", "wrong-secret"), ("in-2", Secret)]);
        var headers = Headers(peer, Now, "{}", Secret, keyId: null);

        var result = Verify(peer, headers);

        Assert.Equal(HmacVerdict.Verified, result.Verdict);
        Assert.Equal("in-2", result.MatchedKeyId);
    }

    // THE VERIFIER SIGNS THE TIMESTAMP IT RECEIVED, NEVER A REFORMATTED ONE. themia-hmac-v1 requires
    // senders to emit a trailing Z, but a sender that emits +00:00 must still verify: the canonical string
    // is the literal header value, and the parse exists only to place the request in the skew window.
    //
    // This is not hypothetical. ezy-assets' marketplace signer emitted +00:00 from the day the channel was
    // built until 2026-08-08 (coord #0069) and nothing ever failed, precisely because both verifiers echo.
    // The day a verifier "normalises the timestamp before signing" instead, every inbound request from a
    // non-Z sender 401s — a total, permanent failure indistinguishable from a rotated secret, so the
    // operator rotates the key and nothing changes. Their side pinned this; ours had not.
    [Fact]
    public void Verify_ShouldSucceed_WhenTheSenderEmitsAZeroOffsetInsteadOfZ()
    {
        var peer = Peer();
        const string OffsetTimestamp = "2026-07-14T09:30:00.0000000+00:00";
        Assert.NotEqual(OffsetTimestamp, ThemiaHmacV1.FormatTimestamp(Now));

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [peer.HeaderNames.Timestamp] = OffsetTimestamp,
            [peer.HeaderNames.Signature] = ThemiaHmacV1.Sign(
                ThemiaHmacV1.Canonicalize(
                    OffsetTimestamp, "PUT", "/api/v1/ingest/listings", "{}"),
                Secret),
        };

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    // The same rule at the other end of the format range: a naive timestamp (no designator at all) is read
    // as UTC for the window, and signed as the literal it arrived as.
    [Fact]
    public void Verify_ShouldSucceed_WhenTheSenderEmitsANaiveTimestamp()
    {
        var peer = Peer();
        const string NaiveTimestamp = "2026-07-14T09:30:00.0000000";

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [peer.HeaderNames.Timestamp] = NaiveTimestamp,
            [peer.HeaderNames.Signature] = ThemiaHmacV1.Sign(
                ThemiaHmacV1.Canonicalize(
                    NaiveTimestamp, "PUT", "/api/v1/ingest/listings", "{}"),
                Secret),
        };

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    // Absence must mean v1 specifically, never "newest" — otherwise adding v2 later silently
    // reinterprets every legacy request that predates the header.
    [Fact]
    public void Verify_ShouldAssumeV1_WhenSchemeHeaderIsAbsent()
    {
        var peer = Peer();

        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret, scheme: null)).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnUnknownScheme_WhenSchemeHeaderIsUnrecognised()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.UnknownScheme,
            Verify(peer, Headers(peer, Now, "{}", Secret, scheme: "themia-hmac-v2")).Verdict);
    }

    // A clock problem is infrastructure and self-heals; it must be distinguishable from a bad signature so
    // the sender can retry rather than dead-letter. This verdict maps to 408, not 401.
    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public void Verify_ShouldReturnStaleTimestamp_WhenOutsideTheWindow(int offsetSeconds)
    {
        var peer = Peer();
        var headers = Headers(peer, Now.AddSeconds(offsetSeconds), "{}", Secret);

        Assert.Equal(HmacVerdict.StaleTimestamp, Verify(peer, headers).Verdict);
    }

    [Theory]
    [InlineData(-299)]
    [InlineData(299)]
    public void Verify_ShouldSucceed_JustInsideTheWindow(int offsetSeconds)
    {
        var peer = Peer();
        var headers = Headers(peer, Now.AddSeconds(offsetSeconds), "{}", Secret);

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    // Malformed input never becomes valid by retrying, so it is NOT stale — it maps to 401.
    [Fact]
    public void Verify_ShouldReturnMalformedTimestamp_WhenUnparseable()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);
        headers[peer.HeaderNames.Timestamp] = "not-a-timestamp";

        Assert.Equal(HmacVerdict.MalformedTimestamp, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnMalformedTimestamp_WhenHeaderIsMissing()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);
        headers.Remove(peer.HeaderNames.Timestamp);

        Assert.Equal(HmacVerdict.MalformedTimestamp, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnUnknownKeyId_WhenKeyIdIsPresentButUnconfigured()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.UnknownKeyId,
            Verify(peer, Headers(peer, Now, "{}", Secret, keyId: "nope")).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheBodyIsTampered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);

        Assert.Equal(HmacVerdict.SignatureMismatch, Verify(peer, headers, body: "{\"evil\":1}").Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenThePathIsTampered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);

        Assert.Equal(HmacVerdict.SignatureMismatch, Verify(peer, headers, path: "/api/v1/ingest/other").Verdict);
    }

    // Query order is signed as-sent, so reordering must break the signature.
    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheQueryIsReordered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "", Secret, method: "DELETE", path: "/x?a=1&b=2");

        var result = Verify(peer, headers, body: "", method: "DELETE", path: "/x?b=2&a=1");

        Assert.Equal(HmacVerdict.SignatureMismatch, result.Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheSecretIsWrong()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.SignatureMismatch,
            Verify(peer, Headers(peer, Now, "{}", "different-secret")).Verdict);
    }

    [Fact]
    public void Verify_ShouldHonourACustomHeaderPrefix()
    {
        var peer = Peer(prefix: "X-Propertiezy-");

        Assert.Equal("X-Propertiezy-Signature", peer.HeaderNames.Signature);
        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret)).Verdict);
    }
}
