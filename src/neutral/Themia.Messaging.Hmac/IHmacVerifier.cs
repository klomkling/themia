namespace Themia.Messaging.Hmac;

/// <summary>Verifies an inbound request against a peer's configured keys.</summary>
public interface IHmacVerifier
{
    /// <summary>Verifies a request's timestamp and signature against <paramref name="peer"/>.</summary>
    /// <param name="peer">The peer whose keys and clock skew tolerance apply.</param>
    /// <param name="headers">The inbound request headers, keyed case-insensitively.</param>
    /// <param name="method">The HTTP method, as sent.</param>
    /// <param name="pathAndQuery">The path and query, exactly as sent.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="now">The current instant, used to evaluate clock skew.</param>
    /// <returns>The verification verdict, the matched key id when verified, and the computed skew when relevant.</returns>
    HmacVerificationResult Verify(
        MessagingPeer peer,
        IReadOnlyDictionary<string, string?> headers,
        string method,
        string pathAndQuery,
        string body,
        DateTimeOffset now);
}
