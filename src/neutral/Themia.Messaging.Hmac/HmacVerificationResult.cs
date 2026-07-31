namespace Themia.Messaging.Hmac;

/// <summary>
/// The verdict produced by <see cref="IHmacVerifier.Verify"/>. Deliberately distinct values for
/// <see cref="StaleTimestamp"/> and <see cref="MalformedTimestamp"/>: a clock problem is infrastructure and
/// self-heals on retry, while malformed input never becomes valid by retrying — collapsing them into one
/// verdict reintroduces the bug where both live implementations independently shipped 401-for-clock-skew
/// and every message dead-lettered on the first attempt.
/// </summary>
public enum HmacVerdict
{
    /// <summary>The request's signature verified successfully.</summary>
    Verified = 0,

    /// <summary>The timestamp fell outside the peer's configured clock skew tolerance. Maps to HTTP 408 — retryable.</summary>
    StaleTimestamp = 1,

    /// <summary>The timestamp header was missing or unparseable. Maps to HTTP 401 — not retryable.</summary>
    MalformedTimestamp = 2,

    /// <summary>The key-id header named a key the peer has not configured as inbound. Maps to HTTP 401.</summary>
    UnknownKeyId = 3,

    /// <summary>No configured inbound key produced a matching signature. Maps to HTTP 401.</summary>
    SignatureMismatch = 4,

    /// <summary>The scheme header named a scheme this verifier does not recognise. Maps to HTTP 400.</summary>
    UnknownScheme = 5,

    /// <summary>No peer was configured under the requested name.</summary>
    UnknownPeer = 6,
}

/// <summary>The outcome of <see cref="IHmacVerifier.Verify"/>.</summary>
/// <param name="Verdict">The verification verdict.</param>
/// <param name="MatchedKeyId">The inbound key id that verified, set only when <paramref name="Verdict"/> is <see cref="HmacVerdict.Verified"/>.</param>
/// <param name="Skew">The signed difference between now and the sender's timestamp, set only when computed.</param>
public readonly record struct HmacVerificationResult(HmacVerdict Verdict, string? MatchedKeyId = null, TimeSpan? Skew = null)
{
    /// <summary>The request verified successfully against <paramref name="matchedKeyId"/>.</summary>
    /// <param name="matchedKeyId">The inbound key id that produced a matching signature.</param>
    /// <returns>A <see cref="HmacVerdict.Verified"/> result.</returns>
    public static HmacVerificationResult Verified(string matchedKeyId) => new(HmacVerdict.Verified, matchedKeyId);

    /// <summary>The timestamp fell outside the peer's configured clock skew tolerance.</summary>
    /// <param name="skew">The signed difference between now and the sender's timestamp.</param>
    /// <returns>A <see cref="HmacVerdict.StaleTimestamp"/> result.</returns>
    public static HmacVerificationResult StaleTimestamp(TimeSpan skew) => new(HmacVerdict.StaleTimestamp, Skew: skew);

    /// <summary>The timestamp header was missing or could not be parsed.</summary>
    /// <returns>A <see cref="HmacVerdict.MalformedTimestamp"/> result.</returns>
    public static HmacVerificationResult MalformedTimestamp() => new(HmacVerdict.MalformedTimestamp);

    /// <summary>The key-id header named a key the peer has not configured as inbound.</summary>
    /// <returns>A <see cref="HmacVerdict.UnknownKeyId"/> result.</returns>
    public static HmacVerificationResult UnknownKeyId() => new(HmacVerdict.UnknownKeyId);

    /// <summary>No configured inbound key produced a matching signature.</summary>
    /// <returns>A <see cref="HmacVerdict.SignatureMismatch"/> result.</returns>
    public static HmacVerificationResult SignatureMismatch() => new(HmacVerdict.SignatureMismatch);

    /// <summary>The scheme header named a scheme this verifier does not recognise.</summary>
    /// <returns>A <see cref="HmacVerdict.UnknownScheme"/> result.</returns>
    public static HmacVerificationResult UnknownScheme() => new(HmacVerdict.UnknownScheme);

    /// <summary>No peer was configured under the requested name.</summary>
    /// <returns>A <see cref="HmacVerdict.UnknownPeer"/> result.</returns>
    public static HmacVerificationResult UnknownPeer() => new(HmacVerdict.UnknownPeer);
}
