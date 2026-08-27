namespace Themia.WebAuthn;

/// <summary>
/// Holds a ceremony's options between issuing the challenge and verifying the response, and releases
/// them exactly once.
/// </summary>
/// <remarks>
/// A WebAuthn challenge is single-use by definition: the whole point is that a response can only be
/// produced for a challenge the relying party just issued. The library generates the challenge and
/// verifies the response against the options it was issued with, but it stores nothing — so an
/// integration that keeps the options anywhere reusable accepts the same signed response twice.
/// <para>
/// Nothing about that failure is visible: both sign-ins succeed. It is the WebAuthn counterpart of
/// <c>ITotpReplayStore</c>, and like that one it has <b>no default implementation</b> — a
/// process-local store holds nothing on a second instance, so on any multi-instance deployment every
/// ceremony would fail to find its challenge, or worse, succeed against a stale one.
/// </para>
/// </remarks>
public interface IWebAuthnChallengeStore
{
    /// <summary>Stores the serialized ceremony options against the challenge that identifies them.</summary>
    /// <param name="challengeId">The base64url challenge from the options just issued.</param>
    /// <param name="optionsJson">The serialized options, to be handed back at verification.</param>
    /// <param name="ttl">
    /// How long the ceremony may remain open. Anything older is abandoned.
    /// <para>
    /// <b>On a store that does not expire on its own, this MUST become a predicate inside
    /// <see cref="TryConsumeAsync"/> — a background sweep is not enough.</b> Redis expires the key
    /// itself, so <c>GETDEL</c> cannot return a dead one; SQL expires nothing, and
    /// <c>DELETE … WHERE challenge_id = @id RETURNING …</c> happily hands back a challenge from last
    /// week. The TTL then silently becomes infinite, and the symptom is a <i>successful</i> sign-in
    /// against a stale ceremony — the same shape of invisible failure as the two guards this package
    /// exists for. Write <c>AND expires_at &gt; now()</c> into the consuming statement; a sweep, if
    /// you have one, only reclaims space.
    /// </para>
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    ValueTask StoreAsync(string challengeId, string optionsJson, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Atomically retrieves and removes the options for <paramref name="challengeId"/>.
    /// </summary>
    /// <param name="challengeId">The base64url challenge from the response being verified.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The options, or <see langword="null"/> when the challenge is unknown, expired or already used.</returns>
    /// <remarks>
    /// <b>Retrieve and remove must be one operation.</b> Split into a read followed by a delete, two
    /// concurrent submissions of the same response both read the options before either deletes them,
    /// and both are admitted — which is the replay this store exists to prevent. Redis <c>GETDEL</c>,
    /// or <c>DELETE … RETURNING</c> on a relational store — with the expiry predicate in the same
    /// statement, for the reason given on <c>StoreAsync</c>'s <c>ttl</c>.
    /// </remarks>
    ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct = default);
}
