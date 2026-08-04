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
    /// Returns the rate-limit quota one issuance consumed, for a delivery that is known to have failed
    /// (including <c>NotificationResult.NotConfigured</c>). Decrements both the per-scope and per-key
    /// windows, each floored at zero. A message that was never sent must not consume the victim's
    /// allowance.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent, and identified by challenge rather than by scope.</b> The refund is claimed with a
    /// guarded write against the challenge row, so calling this repeatedly for the same issuance refunds
    /// once and returns <see langword="false"/> thereafter. That matters because the callers are
    /// retry-prone by nature — provider delivery-status webhooks are redelivered, and adopters retry
    /// their own failure handlers — and an unguarded refund is a decrement of the counter that bounds an
    /// SMS bill: replayed enough times it drives the ceiling to zero and issuance becomes unlimited.
    /// Taking the challenge id also lets the window buckets be derived from the row's own creation time,
    /// which is the only thing that identifies the buckets the issuance actually charged.
    /// </remarks>
    /// <param name="challengeId"><see cref="ChallengeIssueResult.ChallengeId"/> from the issuance being refunded.</param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <returns>
    /// <see langword="true"/> if this call performed the refund; <see langword="false"/> if there was
    /// nothing to refund — the challenge was already refunded, or its row no longer exists (retention
    /// deletes challenge rows well before the counters they charged elapse, so this is routine, not an
    /// error).
    /// </returns>
    /// <exception cref="InvalidOperationException">The challenge's purpose is no longer configured via <see cref="ChallengeOptions.ConfigurePurpose"/>.</exception>
    Task<bool> RefundAsync(Guid challengeId, CancellationToken cancellationToken = default);
}
