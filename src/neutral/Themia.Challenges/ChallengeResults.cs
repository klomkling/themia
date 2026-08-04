namespace Themia.Challenges;

/// <summary>
/// How an issue attempt ended. An enum rather than a bare bool: a new state added later (e.g. a
/// third rate-limit tier) compiles cleanly at every existing <c>if (result.Succeeded)</c> call site,
/// so nothing forces a consumer to revisit its handling. A <c>switch</c> over this enum fails to
/// compile when a case is unhandled.
/// </summary>
public enum ChallengeIssueOutcome
{
    /// <summary>A new secret was generated, stored, and returned.</summary>
    Issued,

    /// <summary>Issuance was refused because the scope or key exceeded its rate-limit window.</summary>
    RateLimited,
}

/// <summary>
/// How a verify attempt ended. Deliberately distinguishes <see cref="Expired"/>, <see cref="Consumed"/>
/// and <see cref="NotFound"/> rather than collapsing them into one failure, because callers such as an
/// audit log or a "resend" prompt behave differently for each: an expired code invites a resend, a
/// consumed one signals replay, and a not-found one may mean the key was never challenged at all.
/// <para>
/// <b>Do not map these outcomes to distinguishable responses on an unauthenticated endpoint.</b> The
/// distinction that makes this enum useful internally is also an account-enumeration oracle when it
/// reaches an anonymous caller: <see cref="Consumed"/> or <see cref="Expired"/> means a challenge was
/// once issued for that key and <see cref="NotFound"/> means none ever was, so an attacker submitting
/// any wrong code to a login or password-reset endpoint learns whether the phone number or email
/// address is registered — without ever needing a valid code. Branch on these values for logging,
/// metrics, and internal flow control; return one indistinguishable failure (same status, same body,
/// same timing) to the caller. An authenticated endpoint, where the caller already owns the key, has
/// nothing to leak and may surface the difference.
/// </para>
/// </summary>
public enum ChallengeVerifyOutcome
{
    /// <summary>The submitted secret matched and the challenge is now consumed.</summary>
    Verified,

    /// <summary>The submitted secret did not match. The attempt counts against <c>MaxAttempts</c>.</summary>
    Incorrect,

    /// <summary>The challenge's TTL elapsed before verification was attempted.</summary>
    Expired,

    /// <summary>The challenge was already verified once. Secrets verify exactly once.</summary>
    Consumed,

    /// <summary>The challenge's attempt cap was reached before a correct secret was submitted.</summary>
    AttemptsExhausted,

    /// <summary>No challenge is outstanding for the scope — it was never issued, or already expired
    /// and purged.</summary>
    NotFound,

    /// <summary>
    /// Verification was refused before any lookup because the key exceeded
    /// <see cref="ChallengeOptions.VerifyWindow"/>. No challenge was read and no attempt was recorded,
    /// so this does not count against <see cref="PurposeOptions.MaxAttempts"/> — nothing was compared.
    /// </summary>
    RateLimited,
}

/// <summary>The result of issuing a challenge.</summary>
public sealed class ChallengeIssueResult
{
    private ChallengeIssueResult(ChallengeIssueOutcome outcome, Guid? challengeId, string? secret, DateTimeOffset? expiresAt)
    {
        Outcome = outcome;
        ChallengeId = challengeId;
        Secret = secret;
        ExpiresAt = expiresAt;
    }

    /// <summary>How the issue attempt ended. Switch over this rather than reading <see cref="Succeeded"/>
    /// when the states need different handling.</summary>
    public ChallengeIssueOutcome Outcome { get; }

    /// <summary>
    /// The plaintext secret — non-null only when <see cref="Outcome"/> is <see cref="ChallengeIssueOutcome.Issued"/>.
    /// This is the single moment the plaintext exists outside the delivery channel: the store persists only
    /// a hash of it, so once this result is handed to the caller (to send by SMS, email, or link), the
    /// plaintext cannot be recovered again. Never log this value.
    /// </summary>
    public string? Secret { get; }

    /// <summary>When the issued secret expires, when <see cref="Outcome"/> is
    /// <see cref="ChallengeIssueOutcome.Issued"/>; otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Identifies the stored challenge, when <see cref="Outcome"/> is
    /// <see cref="ChallengeIssueOutcome.Issued"/>; otherwise <see langword="null"/>. Pass it to
    /// <see cref="IChallengeService.RefundAsync"/> if delivery of this secret turns out to have failed.
    /// It is a handle, not a credential: verification is by secret or token hash, so knowing an id
    /// grants nothing. Refund is keyed on it rather than on the scope so the refund can be made
    /// once-only, and so the rate-limit buckets to credit are read from the row's own creation time
    /// instead of being re-derived by the caller.
    /// </summary>
    public Guid? ChallengeId { get; }

    /// <summary>Whether a secret was issued.</summary>
    public bool Succeeded => Outcome == ChallengeIssueOutcome.Issued;

    /// <summary>Creates an <see cref="ChallengeIssueOutcome.Issued"/> result.</summary>
    /// <param name="challengeId">The stored challenge's id — the value <see cref="IChallengeService.RefundAsync"/> needs.</param>
    /// <param name="secret">The plaintext secret handed to the caller for delivery.</param>
    /// <param name="expiresAt">When the secret expires.</param>
    /// <returns>An issued result carrying the plaintext secret.</returns>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="challengeId"/> is <see cref="Guid.Empty"/>.</exception>
    public static ChallengeIssueResult Issued(Guid challengeId, string secret, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (challengeId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(challengeId), "An issued challenge must carry a real id.");
        }

        return new ChallengeIssueResult(ChallengeIssueOutcome.Issued, challengeId, secret, expiresAt);
    }

    /// <summary>Creates a <see cref="ChallengeIssueOutcome.RateLimited"/> result.</summary>
    /// <returns>A rate-limited result. No secret is generated.</returns>
    public static ChallengeIssueResult RateLimited() => new(ChallengeIssueOutcome.RateLimited, null, null, null);
}

/// <summary>The result of verifying a challenge.</summary>
public sealed class ChallengeVerifyResult
{
    private ChallengeVerifyResult(ChallengeVerifyOutcome outcome, ChallengeScope scope)
    {
        Outcome = outcome;
        Scope = scope;
    }

    /// <summary>How the verify attempt ended. Switch over this rather than reading <see cref="Succeeded"/>
    /// when the states need different handling.</summary>
    public ChallengeVerifyOutcome Outcome { get; }

    /// <summary>The scope that was verified against.</summary>
    public ChallengeScope Scope { get; }

    /// <summary>Whether the submitted secret was correct.</summary>
    public bool Succeeded => Outcome == ChallengeVerifyOutcome.Verified;

    /// <summary>Creates a <see cref="ChallengeVerifyOutcome.Verified"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>A verified result.</returns>
    public static ChallengeVerifyResult Verified(ChallengeScope scope) => new(ChallengeVerifyOutcome.Verified, scope);

    /// <summary>Creates an <see cref="ChallengeVerifyOutcome.Incorrect"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>An incorrect-secret result.</returns>
    public static ChallengeVerifyResult Incorrect(ChallengeScope scope) => new(ChallengeVerifyOutcome.Incorrect, scope);

    /// <summary>Creates an <see cref="ChallengeVerifyOutcome.Expired"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>An expired result.</returns>
    public static ChallengeVerifyResult Expired(ChallengeScope scope) => new(ChallengeVerifyOutcome.Expired, scope);

    /// <summary>Creates a <see cref="ChallengeVerifyOutcome.Consumed"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>An already-consumed result.</returns>
    public static ChallengeVerifyResult Consumed(ChallengeScope scope) => new(ChallengeVerifyOutcome.Consumed, scope);

    /// <summary>Creates an <see cref="ChallengeVerifyOutcome.AttemptsExhausted"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>An attempts-exhausted result.</returns>
    public static ChallengeVerifyResult AttemptsExhausted(ChallengeScope scope) => new(ChallengeVerifyOutcome.AttemptsExhausted, scope);

    /// <summary>Creates a <see cref="ChallengeVerifyOutcome.RateLimited"/> result.</summary>
    /// <param name="scope">The scope whose verification was refused.</param>
    /// <returns>A rate-limited result. No challenge was read.</returns>
    public static ChallengeVerifyResult RateLimited(ChallengeScope scope) => new(ChallengeVerifyOutcome.RateLimited, scope);

    /// <summary>Creates a <see cref="ChallengeVerifyOutcome.NotFound"/> result.</summary>
    /// <param name="scope">The scope that was verified.</param>
    /// <returns>A not-found result.</returns>
    public static ChallengeVerifyResult NotFound(ChallengeScope scope) => new(ChallengeVerifyOutcome.NotFound, scope);
}
