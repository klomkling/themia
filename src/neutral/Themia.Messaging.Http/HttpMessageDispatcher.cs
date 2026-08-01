using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Themia.Messaging.Hmac;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.Http;

/// <summary>
/// Delivers a claimed outbox row over HTTP, signed with <c>themia-hmac-v1</c>. Resolves the destination
/// peer and its configured route, sends <see cref="ClaimedMessageRow.Payload"/> verbatim, and classifies
/// the response via <see cref="HttpStatusClassifier"/>. Owns no retry, backoff or circuit-breaker logic —
/// that is the outbox drainer's job; a second retry layer here would make <c>MaxAttempts</c> meaningless.
/// </summary>
public sealed class HttpMessageDispatcher : IOutboxDispatcher<ClaimedMessageRow>
{
    private const string ContentType = "application/json";
    private const string HttpPostMethod = "POST";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly HmacOptions hmacOptions;
    private readonly ILogger<HttpMessageDispatcher> logger;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="httpClientFactory">Creates a named <see cref="HttpClient"/> per peer (the peer's name is the client name).</param>
    /// <param name="hmacOptions">The registered peers, their signing keys and outbound routes.</param>
    /// <param name="logger">Logger for delivery outcomes. Never receives the secret, the signature or the payload.</param>
    /// <exception cref="ArgumentNullException">Any parameter is <see langword="null"/>.</exception>
    public HttpMessageDispatcher(IHttpClientFactory httpClientFactory, HmacOptions hmacOptions, ILogger<HttpMessageDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hmacOptions);
        ArgumentNullException.ThrowIfNull(logger);

        this.httpClientFactory = httpClientFactory;
        this.hmacOptions = hmacOptions;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="OperationCanceledException"/> triggered by <paramref name="ct"/> propagates rather than
    /// being reported as a result — that is host shutdown, not a delivery failure. A timeout instead
    /// surfaces as a cancellation exception tied to an internal token, with <paramref name="ct"/> left
    /// uncancelled, and is reported as <see cref="DispatchOutcome.Transient"/>.
    /// </remarks>
    public async Task<DispatchResult> DispatchAsync(IServiceProvider scopedServices, ClaimedMessageRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!hmacOptions.TryGetPeer(row.Destination, out var peer) || peer is null)
        {
            logger.LogWarning("Outbox row {RowId} addressed to unknown peer '{Destination}'.", row.Id, row.Destination);
            return DispatchResult.Permanent($"Unknown destination peer '{row.Destination}'.");
        }

        if (!peer.Routes.TryGetValue(row.Type, out var path))
        {
            logger.LogWarning(
                "Outbox row {RowId} of type '{Type}' has no route configured for peer '{Peer}'.", row.Id, row.Type, peer.Name);
            return DispatchResult.Permanent($"No route configured for message type '{row.Type}' on peer '{peer.Name}'.");
        }

        if (peer.BaseAddress is null)
        {
            logger.LogWarning("Outbox row {RowId} peer '{Peer}' has no base address configured for outbound requests.", row.Id, peer.Name);
            return DispatchResult.Permanent($"Peer '{peer.Name}' has no base address configured for outbound requests.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(peer.BaseAddress, path))
        {
            Content = new StringContent(row.Payload, Encoding.UTF8, ContentType),
        };

        Sign(request, peer, row);
        MergeEnvelopeHeaders(request, peer, row);

        var client = httpClientFactory.CreateClient(peer.Name);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Outbox row {RowId} delivery to peer '{Peer}' timed out.", row.Id, peer.Name);
            return DispatchResult.Transient($"Request to peer '{peer.Name}' timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Outbox row {RowId} delivery to peer '{Peer}' failed.", row.Id, peer.Name);
            return DispatchResult.Transient($"Request to peer '{peer.Name}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var outcome = HttpStatusClassifier.Classify(status);
            logger.LogInformation(
                "Outbox row {RowId} delivery to peer '{Peer}' returned {Status} ({Outcome}).", row.Id, peer.Name, status, outcome);

            return outcome switch
            {
                DispatchOutcome.Delivered => DispatchResult.Delivered(),
                DispatchOutcome.Transient => DispatchResult.Transient($"Peer '{peer.Name}' returned HTTP {status}."),
                _ => DispatchResult.Permanent($"Peer '{peer.Name}' returned HTTP {status}."),
            };
        }
    }

    // pathAndQuery is read back from request.RequestUri (the URI that will actually be sent) rather than
    // reconstructed from peer.BaseAddress + path, so the signed value can never diverge from the sent one.
    private static void Sign(HttpRequestMessage request, MessagingPeer peer, ClaimedMessageRow row)
    {
        var names = peer.HeaderNames;
        var timestamp = ThemiaHmacV1.FormatTimestamp(DateTimeOffset.UtcNow);
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        var canonical = ThemiaHmacV1.Canonicalize(timestamp, HttpPostMethod, pathAndQuery, row.Payload);
        var (keyId, signature) = peer.SignOutbound(canonical);

        request.Headers.TryAddWithoutValidation(names.Timestamp, timestamp);
        request.Headers.TryAddWithoutValidation(names.Signature, signature);
        request.Headers.TryAddWithoutValidation(names.KeyId, keyId);
        request.Headers.TryAddWithoutValidation(names.Scheme, ThemiaHmacV1.SchemeName);
        request.Headers.TryAddWithoutValidation(names.Origin, row.Origin);
    }

    // The envelope's Headers JSON is adopter-supplied transport metadata (see MessageEnvelope.Headers) and
    // must never be able to clobber a signature header — that would let a crafted envelope forge the
    // key-id or scheme a receiver verifies against.
    private static void MergeEnvelopeHeaders(HttpRequestMessage request, MessagingPeer peer, ClaimedMessageRow row)
    {
        if (string.IsNullOrEmpty(row.Headers))
        {
            return;
        }

        Dictionary<string, string?>? envelopeHeaders;
        try
        {
            envelopeHeaders = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.Headers);
        }
        catch (JsonException)
        {
            // Malformed envelope headers must not block delivery of an otherwise valid, signed row.
            return;
        }

        if (envelopeHeaders is null)
        {
            return;
        }

        var names = peer.HeaderNames;
        var reserved = new[] { names.Timestamp, names.Signature, names.KeyId, names.Scheme, names.Origin };

        foreach (var (key, value) in envelopeHeaders)
        {
            if (value is null || reserved.Any(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // TryAddWithoutValidation skips .NET's normal header-value validation (names are token-checked
            // by the public API, but values are not). A control character in an adopter-supplied value can
            // throw at send time from outside the two catch clauses above, escaping DispatchAsync
            // unhandled — so a malformed envelope value is skipped here instead of ever reaching the
            // request, the same "must not block delivery of an otherwise valid, signed row" rule the
            // malformed-JSON case above already follows.
            if (value.Contains('\r') || value.Contains('\n'))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }
    }
}
