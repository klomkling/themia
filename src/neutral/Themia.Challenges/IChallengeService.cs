namespace Themia.Challenges;

/// <summary>
/// Issues a one-time secret bound to an opaque key, verifies it exactly once, and enforces the TTL,
/// attempt cap, and two-layer rate limiting documented on <see cref="ChallengeOptions"/> and
/// <see cref="PurposeOptions"/>. Serves phone OTP, email OTP, and (once <see cref="VerifyByTokenAsync"/>
/// ships) magic links and email verification.
/// </summary>
public interface IChallengeService
{
    /// <summary>
    /// Issues a new secret for <paramref name="scope"/>, subject to both rate-limit layers and the
    /// purpose's re-issue policy. The plaintext secret returned on
    /// <see cref="ChallengeIssueOutcome.Issued"/> is the only time it exists outside the delivery
    /// channel — never logged, and unrecoverable once handed to the caller.
    /// </summary>
    /// <param name="scope">The identity of the challenge — key, purpose, and tenant.</param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <returns>
    /// An <see cref="ChallengeIssueOutcome.Issued"/> result carrying the plaintext secret, or a
    /// <see cref="ChallengeIssueOutcome.RateLimited"/> result when either rate-limit layer refuses —
    /// no secret is generated and no row is written on refusal.
    /// </returns>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/>'s purpose was never configured via <see cref="ChallengeOptions.ConfigurePurpose"/>.</exception>
    Task<ChallengeIssueResult> IssueAsync(ChallengeScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a user-typed secret (a numeric code) against the live challenge for <paramref name="scope"/>.
    /// The caller supplies the key, so the row is found by scope and the secret compared in constant time.
    /// </summary>
    /// <param name="scope">The identity of the challenge being verified.</param>
    /// <param name="code">The plaintext secret the caller submitted.</param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <returns>How the verify attempt ended. See <see cref="ChallengeVerifyOutcome"/> for the full set of outcomes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/>'s purpose was never configured via <see cref="ChallengeOptions.ConfigurePurpose"/>.</exception>
    Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an opaque token (a magic link) by the token's hash alone — the caller has only the
    /// token, not the original key and purpose.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented in v1.</b> <see cref="IssueAsync"/> never populates a token hash, so there is
    /// nothing this method could look up; always throws <see cref="NotSupportedException"/> rather than
    /// <see cref="ChallengeVerifyOutcome.NotFound"/>, which would read as an expired or already-used
    /// token and send an adopter debugging their own storage for a feature that was never wired up.
    /// </remarks>
    /// <param name="token">The plaintext token submitted by the caller.</param>
    /// <param name="purpose">Asserted against the found row; a mismatch is treated as not found (not implemented in v1, so unreachable today).</param>
    /// <param name="tenantId">Asserted against the found row; a mismatch is treated as not found (not implemented in v1, so unreachable today).</param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <exception cref="NotSupportedException">Always — the opaque-token format has no generator in v1.</exception>
    Task<ChallengeVerifyResult> VerifyByTokenAsync(
        string token, string purpose, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the rate-limit quota an issuance consumed, for a delivery that is known to have failed
    /// (including <c>NotificationResult.NotConfigured</c>). Decrements both the per-scope and per-key
    /// windows, each floored at zero. A message that was never sent must not consume the victim's
    /// allowance.
    /// </summary>
    /// <param name="scope">The scope whose issuance should be refunded.</param>
    /// <param name="issuedAt">
    /// <see cref="ChallengeIssueResult.IssuedAt"/> from the issuance being refunded. Required, and not
    /// defaulted to the current time: rate-limit counters are fixed-width buckets keyed by window
    /// start, so a refund computed from "now" targets whichever bucket is live when the failure is
    /// noticed — which, for a delivery failure discovered asynchronously, is routinely not the bucket
    /// the issue charged. That leaves the original charge standing and decrements a stranger's, so the
    /// caller must carry the issuance time rather than let this method guess it.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/>'s purpose was never configured via <see cref="ChallengeOptions.ConfigurePurpose"/>.</exception>
    Task RefundAsync(ChallengeScope scope, DateTimeOffset issuedAt, CancellationToken cancellationToken = default);
}
