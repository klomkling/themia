using System.Security.Cryptography;
using System.Text;

namespace Themia.Messaging.Hmac;

/// <summary>Verifies an inbound request against a peer's configured keys.</summary>
public sealed class HmacVerifier : IHmacVerifier
{
    /// <inheritdoc />
    public HmacVerificationResult Verify(
        MessagingPeer peer,
        IReadOnlyDictionary<string, string?> headers,
        string method,
        string pathAndQuery,
        string body,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(headers);

        var names = peer.HeaderNames;

        // Absence means v1 specifically, never "the newest scheme" — otherwise a future v2 would silently
        // reinterpret legacy traffic that predates the header.
        if (headers.TryGetValue(names.Scheme, out var scheme)
            && !string.IsNullOrEmpty(scheme)
            && !string.Equals(scheme, ThemiaHmacV1.SchemeName, StringComparison.Ordinal))
        {
            return HmacVerificationResult.UnknownScheme();
        }

        headers.TryGetValue(names.Timestamp, out var timestampHeader);
        if (!ThemiaHmacV1.TryParseTimestamp(timestampHeader, out var sentAt))
        {
            // Malformed, not stale: it will never become valid by retrying.
            return HmacVerificationResult.MalformedTimestamp();
        }

        var skew = now - sentAt;
        if (skew.Duration() > peer.ClockSkewTolerance)
        {
            return HmacVerificationResult.StaleTimestamp(skew);
        }

        var candidates = ResolveCandidateKeys(peer, headers, names);
        if (candidates.Count == 0)
        {
            return HmacVerificationResult.UnknownKeyId();
        }

        headers.TryGetValue(names.Signature, out var presented);
        if (string.IsNullOrEmpty(presented))
        {
            return HmacVerificationResult.SignatureMismatch();
        }

        var canonical = ThemiaHmacV1.Canonicalize(timestampHeader!, method, pathAndQuery, body);
        foreach (var (keyId, secret) in candidates)
        {
            var expected = ThemiaHmacV1.Sign(canonical, secret);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented)))
            {
                return HmacVerificationResult.Verified(keyId);
            }
        }

        return HmacVerificationResult.SignatureMismatch();
    }

    // No key-id header means try every configured inbound key. The live link sends no key-id at all, so
    // requiring one would reject the entire existing integration.
    private static IReadOnlyList<KeyValuePair<string, string>> ResolveCandidateKeys(
        MessagingPeer peer, IReadOnlyDictionary<string, string?> headers, HmacHeaderNames names)
    {
        if (headers.TryGetValue(names.KeyId, out var keyId) && !string.IsNullOrEmpty(keyId))
        {
            return peer.InboundKeys.TryGetValue(keyId, out var secret)
                ? [new KeyValuePair<string, string>(keyId, secret)]
                : [];
        }

        return peer.InboundKeys.ToArray();
    }
}
