using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;

using Themia.Messaging.AspNetCore.DependencyInjection;
using Themia.Messaging.Hmac;

namespace Themia.Messaging.AspNetCore;

/// <summary>
/// Minimal-API endpoint filter that verifies an inbound request against a peer's <c>themia-hmac-v1</c>
/// signature, then runs the <see cref="LoopGuard"/>. Attach with
/// <see cref="RouteHandlerBuilderExtensions.RequireThemiaHmac"/>.
/// </summary>
/// <remarks>
/// Order is fixed and load-bearing: size → buffer → scheme → timestamp → key → signature → loop guard
/// last. The size check runs before anything is read; scheme/timestamp/key/signature are resolved
/// together inside <see cref="IHmacVerifier.Verify"/> (in that order) rather than reimplemented here; the
/// loop guard runs only once <see cref="HmacVerdict.Verified"/> comes back, because <c>Origin</c> is
/// attacker-controlled until then.
/// </remarks>
public sealed class HmacVerificationFilter : IEndpointFilter
{
    private readonly HmacOptions hmacOptions;
    private readonly IHmacVerifier verifier;
    private readonly VerificationOptions verificationOptions;
    private readonly TimeProvider time;
    private readonly ILogger<HmacVerificationFilter> logger;

    /// <summary>Creates the filter.</summary>
    /// <param name="hmacOptions">The registered peers, resolved by the name attached via <c>RequireThemiaHmac</c>.</param>
    /// <param name="verifier">Verifies the request's signature; comparison stays inside this dependency.</param>
    /// <param name="verificationOptions">This service's own origin and loop-guard configuration.</param>
    /// <param name="time">Clock used to evaluate the signed timestamp's freshness.</param>
    /// <param name="logger">Logger for rejections. Never receives the secret, the signature or the body.</param>
    /// <exception cref="ArgumentNullException">Any parameter is <see langword="null"/>.</exception>
    public HmacVerificationFilter(
        HmacOptions hmacOptions,
        IHmacVerifier verifier,
        VerificationOptions verificationOptions,
        TimeProvider time,
        ILogger<HmacVerificationFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(hmacOptions);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(verificationOptions);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        this.hmacOptions = hmacOptions;
        this.verifier = verifier;
        this.verificationOptions = verificationOptions;
        this.time = time;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var peerName = httpContext.GetEndpoint()?.Metadata.GetMetadata<ThemiaHmacPeerMetadata>()?.PeerName;
        if (peerName is null || !hmacOptions.TryGetPeer(peerName, out var peer) || peer is null)
        {
            logger.LogError(
                "No themia-hmac peer named '{Peer}' is configured; rejecting the request.", peerName ?? "(none)");
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var request = httpContext.Request;

        // Step 1: size, before reading or hashing anything. Covers the common case where the client
        // declared Content-Length; a chunked request with no declared length falls through to the
        // EnableBuffering bufferLimit backstop below.
        if (request.ContentLength is { } declaredLength && declaredLength > peer.MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Step 2: buffer so the endpoint can read the body again after verification, bounded by
        // MaxBodyBytes so an unbounded body cannot be forced through this unauthenticated read.
        string body;
        request.EnableBuffering(bufferLimit: peer.MaxBodyBytes);
        try
        {
            using var reader = new StreamReader(
                request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            body = await reader.ReadToEndAsync(httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The bufferLimit backstop tripped — the chunked-request case Content-Length can't catch.
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        request.Body.Position = 0; // rewind for the endpoint

        var headers = ToHeaderDictionary(request.Headers);

        // Steps 3-6 (scheme, timestamp, key, signature) all live inside the verifier, in that order.
        var result = verifier.Verify(peer, headers, request.Method, request.GetEncodedPathAndQuery(), body, time.GetUtcNow());

        var status = MapStatus(result, peer);
        if (status is { } rejected)
        {
            return Results.StatusCode(rejected);
        }

        // Step 7: loop guard, last — Origin is attacker-controlled until verification has passed.
        if (LoopGuard.IsLoopback(headers, peer.HeaderNames, verificationOptions.Origin))
        {
            return Results.StatusCode(StatusCodes.Status200OK);
        }

        return await next(context).ConfigureAwait(false);
    }

    // Returns the rejection status for a non-Verified verdict, or null when verification passed and the
    // pipeline should continue.
    private int? MapStatus(HmacVerificationResult result, MessagingPeer peer)
    {
        switch (result.Verdict)
        {
            case HmacVerdict.Verified:
                return null;

            case HmacVerdict.UnknownScheme:
                return StatusCodes.Status400BadRequest;

            case HmacVerdict.StaleTimestamp:
                // Logged so an operator can tell a clock problem from an attack in one line, per the spec.
                logger.LogWarning(
                    "Rejected stale timestamp from peer '{Peer}': observed skew {Skew}, configured tolerance {Tolerance}.",
                    peer.Name, result.Skew, peer.ClockSkewTolerance);
                return StatusCodes.Status408RequestTimeout;

            case HmacVerdict.MalformedTimestamp:
            case HmacVerdict.UnknownKeyId:
            case HmacVerdict.SignatureMismatch:
            case HmacVerdict.UnknownPeer:
            default:
                return StatusCodes.Status401Unauthorized;
        }
    }

    private static Dictionary<string, string?> ToHeaderDictionary(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            result[key] = value.ToString();
        }

        return result;
    }
}
