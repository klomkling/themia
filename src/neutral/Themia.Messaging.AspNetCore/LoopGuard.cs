using Themia.Messaging.Hmac;

namespace Themia.Messaging.AspNetCore;

/// <summary>
/// Detects a message that has come back to its own sender by comparing the inbound <c>Origin</c> header
/// against this service's configured identity.
/// </summary>
/// <remarks>
/// This check is only safe to run AFTER a request has verified. <c>Origin</c> is an unsigned selector
/// header — see <see cref="HmacHeaderNames.Origin"/> — so until the signature has been checked it is
/// attacker-controlled: trusting it earlier would let anyone short-circuit an ingest endpoint by simply
/// claiming to be its owner. <see cref="HmacVerificationFilter"/> calls this only after
/// <see cref="IHmacVerifier.Verify"/> returns <see cref="HmacVerdict.Verified"/>.
/// </remarks>
public static class LoopGuard
{
    /// <summary>Determines whether a verified request's <c>Origin</c> header names this service itself.</summary>
    /// <param name="headers">The verified request's headers.</param>
    /// <param name="headerNames">The peer's header names, used to locate the <c>Origin</c> header.</param>
    /// <param name="ownOrigin">
    /// This service's own configured origin (<see cref="VerificationOptions.Origin"/>). The guard is
    /// inactive — always returns <see langword="false"/> — when this is <see langword="null"/> or empty.
    /// </param>
    /// <returns><see langword="true"/> when the request has looped back to its own origin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> or <paramref name="headerNames"/> is null.</exception>
    public static bool IsLoopback(IReadOnlyDictionary<string, string?> headers, HmacHeaderNames headerNames, string? ownOrigin)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(headerNames);

        if (string.IsNullOrEmpty(ownOrigin))
        {
            return false;
        }

        return headers.TryGetValue(headerNames.Origin, out var origin)
            && !string.IsNullOrEmpty(origin)
            && string.Equals(origin, ownOrigin, StringComparison.Ordinal);
    }
}
