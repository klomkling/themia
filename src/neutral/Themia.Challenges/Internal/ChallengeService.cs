using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Challenges.Internal;

/// <summary>
/// The policy engine: orchestrates <see cref="IChallengeDialect"/>'s twelve statements into
/// <see cref="IChallengeService"/>'s four operations, enforcing every security requirement the package
/// exists for (see <c>docs/superpowers/specs/2026-08-04-themia-challenges-design.md</c>, "Security
/// requirements — non-negotiable").
/// </summary>
/// <remarks>
/// <para>
/// <b>Rate-limit windows are fixed buckets, not sliding.</b> A window's start is computed by flooring
/// <see cref="TimeProvider.GetUtcNow"/> to the configured window length: <c>WindowStart = now -
/// (now.UtcTicks % window.Ticks)</c>, giving every caller the same deterministic bucket boundary
/// (aligned to <see cref="DateTimeOffset.MinValue"/>, not to any per-scope epoch) rather than one that
/// depends on when the scope first issued. This was chosen over a sliding window because
/// <see cref="IChallengeDialect.IncrementWindowSql"/> and <see cref="IChallengeDialect.DecrementWindowSql"/>
/// are both keyed by a single <c>window_start</c> value per bucket — a sliding window needs either a
/// rolling log of individual issuance timestamps (a third table) or an approximation, neither of which
/// this schema carries. The accepted tradeoff is bucket-boundary bursting: a caller can issue up to
/// <c>Limit</c> secrets in the last second of one bucket and <c>Limit</c> more in the first second of
/// the next, i.e. up to <c>2 * Limit</c> in a short span straddling the boundary. This is acceptable
/// here because the per-key ceiling exists to bound cost, not to stop brute force (see
/// <see cref="ChallengeOptions.PerKeyWindow"/>'s remarks) — a fixed bucket's worst-case burst is still a
/// small, bounded multiple of the configured limit, not an unbounded one.
/// </para>
/// <para>
/// <b><see cref="VerifyAsync"/> checks every live row for the scope, not just the newest.</b>
/// <see cref="IChallengeDialect.SelectLiveByScopeSql"/> returns every unconsumed, unexpired row
/// (<c>created_at DESC</c>) — this is load-bearing, not incidental: <see cref="PurposeOptions.MaxLiveChallenges"/>
/// exists specifically so a late-arriving first code (see its remarks) can still verify after a resend,
/// and that is only true if verification can see it. A row that has already hit
/// <see cref="PurposeOptions.MaxAttempts"/> is excluded from matching (a correct-but-late guess must not
/// revive it — see <see cref="VerifyAsync"/>'s own comments); on a mismatch, the failed guess is recorded
/// against every remaining guessable row, not just the newest, so a wider brute-force surface never opens
/// up as a side effect of raising <see cref="PurposeOptions.MaxLiveChallenges"/>, and
/// <see cref="ChallengeVerifyOutcome.AttemptsExhausted"/> is reported only once every live row has hit
/// its cap.
/// </para>
/// <para>
/// <b>Known limitation: the re-issue policy cannot enforce <c>MaxLiveChallenges &gt; 1</c> as an exact
/// ceiling.</b> It invalidates outstanding challenges only when <see cref="PurposeOptions.MaxLiveChallenges"/>
/// is 1, because the statement that would enforce a higher cap — invalidate all but the newest
/// <c>N - 1</c> — does not exist; <see cref="IChallengeDialect.InvalidateLiveForScopeSql"/> only supports
/// invalidating every live row for a scope, not a chosen subset of them. Above 1, live count is bounded
/// only loosely, by the per-scope rate-limit window plus each challenge's own TTL. This is a real gap
/// (unlike the verification limitation above, which this revision closed) — a future statement could
/// close it, but "invalidate everything or nothing" is what the shipped dialects currently offer.
/// </para>
/// </remarks>
internal sealed class ChallengeService : IChallengeService
{
    private readonly IChallengeDialect dialect;
    private readonly ChallengeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ChallengeService> logger;

    static ChallengeService()
    {
        // Every shipped dialect's SELECT statements return the raw snake_case columns from the shared
        // schema (see ChallengeRow's remarks) — fold out underscores once, scoped to this type only, so
        // dialect authors never have to alias columns to PascalCase.
        SqlMapper.SetTypeMap(typeof(ChallengeRow), new CustomPropertyTypeMap(typeof(ChallengeRow), MatchColumn));
    }

    /// <summary>Creates the engine over <paramref name="dialect"/>.</summary>
    public ChallengeService(IChallengeDialect dialect, ChallengeOptions options, TimeProvider timeProvider, ILogger<ChallengeService> logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.dialect = dialect;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChallengeIssueResult> IssueAsync(ChallengeScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var purpose = options.GetPurpose(scope.Purpose);
        var now = timeProvider.GetUtcNow();
        var scopeWindowStart = FloorToWindowStart(now, purpose.PerScopeWindow.Window);
        var keyWindowStart = FloorToWindowStart(now, options.PerKeyWindow.Window);

        await using var connection = dialect.CreateConnection();

        // Charge both counters FIRST, then compare each against its limit using the value the
        // increment itself returned. Reading the counts and only then incrementing — the obvious
        // shape, and the one this originally had — is a read-then-act gate with nothing serializing
        // it: 64 callers racing for the same bucket all read the same pre-increment count, all find
        // it under the ceiling, and all issue. The counters would still be exact (no increment is
        // lost) while the limit they exist to enforce is simply not enforced, which is the failure
        // mode an SMS bill notices and a test asserting on the counter total does not. Charging first
        // makes each caller's observed value unique and monotonic, so at most Limit callers can ever
        // see a value at or below the ceiling, whatever the concurrency.
        var scopeCount = await IncrementWindowAsync(connection, scope, scope.Purpose, scopeWindowStart, cancellationToken);
        var keyCount = await IncrementWindowAsync(connection, scope, null, keyWindowStart, cancellationToken);

        // Both layers are required, not alternatives: per-key is the cost ceiling across every purpose
        // for this key (the layer that protects the invoice), per-scope is the narrower UX-facing one.
        if (keyCount > options.PerKeyWindow.Limit || scopeCount > purpose.PerScopeWindow.Limit)
        {
            // Hand both charges back: no secret was generated and no challenge row was written, so a
            // refused issue must not consume quota — otherwise a caller refused by the per-scope limit
            // would still burn the per-key ceiling, and repeated refusals would compound into a lockout
            // that outlasts the window that produced it. The refund is best-effort by construction:
            // DecrementWindowSql floors at zero, so a concurrent caller can briefly observe this
            // caller's un-refunded +1 and be refused when it need not have been. That direction is the
            // safe one (it over-refuses, never over-admits) and it self-corrects within the round trip.
            await DecrementWindowAsync(connection, scope, scope.Purpose, scopeWindowStart, cancellationToken);
            await DecrementWindowAsync(connection, scope, null, keyWindowStart, cancellationToken);

            logger.LogInformation("Challenge issue rate-limited for purpose {Purpose}", scope.Purpose);
            return ChallengeIssueResult.RateLimited();
        }

        // Re-issue policy: only MaxLiveChallenges == 1 is exactly enforceable with this dialect's
        // statements (see the type-level remarks) — invalidating on every issue is safe even when
        // nothing was previously live, since InvalidateLiveForScopeSql is a no-op UPDATE in that case.
        if (purpose.MaxLiveChallenges == 1)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InvalidateLiveForScopeSql,
                new { TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose, Now = now, ConsumedAt = now },
                cancellationToken: cancellationToken));
        }

        var secret = SecretGenerator.Generate(purpose.Format);
        var (hash, salt) = SecretHasher.Hash(secret);
        var expiresAt = now + purpose.Ttl;

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertSql,
            new
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                Key = scope.Key,
                Purpose = scope.Purpose,
                SecretHash = hash,
                SecretSalt = salt,
                TokenHash = (string?)null,
                Attempts = 0,
                ExpiresAt = expiresAt,
                CreatedAt = now,
            },
            cancellationToken: cancellationToken));

        logger.LogInformation("Challenge issued for purpose {Purpose}", scope.Purpose);
        return ChallengeIssueResult.Issued(secret, expiresAt, now);
    }

    /// <summary>
    /// Charges one rate-limit bucket and returns its count <em>after</em> this increment. The returned
    /// value is the caller's own increment, produced by the same statement that wrote it — see
    /// <see cref="IChallengeDialect.IncrementWindowSql"/> for why a separate read would not be.
    /// </summary>
    private Task<int> IncrementWindowAsync(
        System.Data.Common.DbConnection connection,
        ChallengeScope scope,
        string? purpose,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            dialect.IncrementWindowSql,
            new { Id = Guid.NewGuid(), TenantId = scope.TenantId, Key = scope.Key, Purpose = purpose, WindowStart = windowStart },
            cancellationToken: cancellationToken));

    private Task DecrementWindowAsync(
        System.Data.Common.DbConnection connection,
        ChallengeScope scope,
        string? purpose,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            dialect.DecrementWindowSql,
            new { TenantId = scope.TenantId, Key = scope.Key, Purpose = purpose, WindowStart = windowStart },
            cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(code);

        var purpose = options.GetPurpose(scope.Purpose);
        var now = timeProvider.GetUtcNow();

        await using var connection = dialect.CreateConnection();

        // Every live row for the scope, newest first — not just one. With the default
        // MaxLiveChallenges = 1 this is a single row as before; with a higher cap, a late-arriving
        // first code (the re-issue policy's entire reason to exist — see PurposeOptions.MaxLiveChallenges'
        // remarks) must still be checkable here, or raising the cap would change nothing observable.
        var liveRows = (await connection.QueryAsync<ChallengeRow>(new CommandDefinition(
            dialect.SelectLiveByScopeSql, LiveByScopeParams(scope, now), cancellationToken: cancellationToken))).AsList();

        if (liveRows.Count == 0)
        {
            var outcome = await ClassifyMissingAsync(connection, scope, cancellationToken);
            logger.LogInformation("Challenge verify {Outcome} for purpose {Purpose}", outcome.Outcome, scope.Purpose);
            return outcome;
        }

        // Rows that have already hit the cap from earlier guesses are excluded from matching entirely —
        // not just reported as exhausted after the fact. Comparing the secret against an already-
        // exhausted row and letting a correct-but-late guess still succeed would make MaxAttempts purely
        // advisory: an attacker who keeps guessing past "exhausted" would eventually get through anyway.
        // Once a row hits its cap it is dead for verification, the same as Consumed or Expired, even
        // though it remains a live (unconsumed, unexpired) row in storage.
        var guessable = liveRows.Where(row => row.Attempts < purpose.MaxAttempts).ToList();

        foreach (var row in guessable)
        {
            if (!SecretHasher.Verify(code, row.SecretHash, row.SecretSalt))
            {
                continue;
            }

            var consumedRows = await connection.ExecuteAsync(new CommandDefinition(
                dialect.ConsumeSql, new { row.Id, Now = now, ConsumedAt = now }, cancellationToken: cancellationToken));

            // Rows-affected 0 means someone else's concurrent VerifyAsync already consumed this exact
            // row between our SELECT and our UPDATE — they won the race. Reporting Verified here too
            // would let the same secret succeed twice.
            var matchOutcome = consumedRows > 0 ? ChallengeVerifyResult.Verified(scope) : ChallengeVerifyResult.Consumed(scope);
            logger.LogInformation("Challenge verify {Outcome} for purpose {Purpose}", matchOutcome.Outcome, scope.Purpose);
            return matchOutcome;
        }

        // No guessable row matched (there may be none left, or none of them matched). Attempt
        // accounting: record the failed guess against every still-guessable live row, not just one. The
        // attempt cap is a brute-force defence (design spec, "Security requirements" #3) — if a wrong
        // guess only counted against the newest row, an attacker could burn MaxAttempts guesses against
        // it for free while an older still-live row's counter stayed untouched, then switch targets and
        // get a fresh MaxAttempts budget there. Charging every guessable row in lockstep keeps the total
        // brute-force surface at MaxAttempts regardless of how many challenges are live, matching the
        // design spec's re-issue-policy note that "the brute-force surface does not widen" with a higher
        // MaxLiveChallenges. Already-exhausted rows are skipped here too — incrementing a dead row's
        // counter further serves no purpose.
        foreach (var row in guessable)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.RecordAttemptSql, new { row.Id }, cancellationToken: cancellationToken));
        }

        // Exhausted only once every live row — the ones just incremented, and any that were already
        // exhausted before this call — has hit the cap. As long as one guessable row remains, the caller
        // has a genuine remaining path to success and reporting AttemptsExhausted would be wrong for it,
        // not just imprecise.
        // (row.Attempts + 1 covers both cases: for a just-incremented guessable row it reflects the new
        // count; for an already-exhausted row, Attempts was already >= MaxAttempts, so Attempts + 1 is
        // too — no separate branch needed.)
        var allExhausted = liveRows.TrueForAll(row => row.Attempts + 1 >= purpose.MaxAttempts);
        var mismatchOutcome = allExhausted
            ? ChallengeVerifyResult.AttemptsExhausted(scope)
            : ChallengeVerifyResult.Incorrect(scope);
        logger.LogInformation("Challenge verify {Outcome} for purpose {Purpose}", mismatchOutcome.Outcome, scope.Purpose);
        return mismatchOutcome;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always throws — see <see cref="IChallengeService.VerifyByTokenAsync"/>'s remarks. Never touches
    /// the dialect: <see cref="IssueAsync"/> never populates a token hash, so there is nothing to look up.
    /// </remarks>
    public Task<ChallengeVerifyResult> VerifyByTokenAsync(
        string token, string purpose, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "VerifyByTokenAsync is not implemented in this release of Themia.Challenges: the opaque-token " +
            "(ChallengeFormatKind.OpaqueToken) verification path has no generator wired into IssueAsync, so " +
            "no challenge row ever carries a token hash for this method to look up. Use IssueAsync/VerifyAsync " +
            "with ChallengeFormat.Numeric until opaque-token issuance ships.");
    }

    /// <inheritdoc />
    public async Task RefundAsync(ChallengeScope scope, DateTimeOffset issuedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var purpose = options.GetPurpose(scope.Purpose);

        // Floored from the issuance time, never from "now". Buckets are fixed-width, so a refund that
        // arrives after the boundary the issue fell on — the common case, since delivery failure is
        // discovered asynchronously — would otherwise decrement a bucket the issue never charged:
        // the original charge stays for the rest of its window, and an unrelated live bucket loses a
        // count that belongs to somebody else's issuance. Both directions are wrong, and the second
        // is a quota bypass.
        var scopeWindowStart = FloorToWindowStart(issuedAt, purpose.PerScopeWindow.Window);
        var keyWindowStart = FloorToWindowStart(issuedAt, options.PerKeyWindow.Window);

        await using var connection = dialect.CreateConnection();

        // Both layers, mirroring IssueAsync's two IncrementWindowSql calls — a refund undoes exactly
        // what an issue charged. Each is independently floored at zero by the dialect.
        await DecrementWindowAsync(connection, scope, scope.Purpose, scopeWindowStart, cancellationToken);
        await DecrementWindowAsync(connection, scope, null, keyWindowStart, cancellationToken);

        logger.LogInformation("Challenge quota refunded for purpose {Purpose}", scope.Purpose);
    }

    /// <summary>
    /// Distinguishes <see cref="ChallengeVerifyOutcome.Consumed"/>, <see cref="ChallengeVerifyOutcome.Expired"/>
    /// and <see cref="ChallengeVerifyOutcome.NotFound"/> when the live lookup found nothing live for the
    /// scope. Uses <see cref="IChallengeDialect.SelectMostRecentByScopeSql"/> — the one statement with no
    /// liveness filter at all — rather than re-querying <see cref="IChallengeDialect.SelectLiveByScopeSql"/>:
    /// that statement's own <c>consumed_at IS NULL</c> filter cannot be defeated by any parameter the way
    /// its <c>expires_at &gt; @Now</c> filter can be defeated with <see cref="DateTimeOffset.MinValue"/>,
    /// so it can never tell a consumed row apart from one that never existed — a plain sequential
    /// re-verify of an already-consumed challenge (a double-submitted form, a refresh after success) has
    /// no live row and no way to distinguish "already used" from "never issued" through that path alone.
    /// This previously reported <see cref="ChallengeVerifyOutcome.NotFound"/> for that case; both are
    /// distinct, meaningful outcomes a caller must be able to tell apart (design spec, "Public API"), and
    /// collapsing them cost callers who build alerting or rate-limit logic on <c>NotFound</c> a spike of
    /// false positives from ordinary double-submits.
    /// </summary>
    private async Task<ChallengeVerifyResult> ClassifyMissingAsync(
        System.Data.Common.DbConnection connection, ChallengeScope scope, CancellationToken cancellationToken)
    {
        var mostRecent = await connection.QueryFirstOrDefaultAsync<ChallengeRow>(new CommandDefinition(
            dialect.SelectMostRecentByScopeSql,
            new { TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose },
            cancellationToken: cancellationToken));

        if (mostRecent is null)
        {
            return ChallengeVerifyResult.NotFound(scope);
        }

        // consumed_at is set both by a genuine ConsumeSql (real verification) and by
        // InvalidateLiveForScopeSql's re-issue supersession — both mean "this exact code no longer
        // verifies", which is what Consumed communicates to the caller regardless of which set it.
        return mostRecent.ConsumedAt is not null
            ? ChallengeVerifyResult.Consumed(scope)
            : ChallengeVerifyResult.Expired(scope);
    }

    private static object LiveByScopeParams(ChallengeScope scope, DateTimeOffset now) =>
        new { TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose, Now = now };

    /// <summary>
    /// Fixed-bucket window start — see the type-level remarks for the sliding-vs-fixed rationale.
    /// </summary>
    private static DateTimeOffset FloorToWindowStart(DateTimeOffset now, TimeSpan window)
    {
        var ticks = now.UtcTicks - (now.UtcTicks % window.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    // "tenant_id" -> "TenantId": strip underscores from the column name and match case-insensitively
    // against the property name (also stripped, so this is symmetric regardless of which side has
    // them). Every column in this schema round-trips uniquely this way; see ChallengeRow's remarks.
    private static System.Reflection.PropertyInfo MatchColumn(Type type, string columnName)
    {
        var normalized = columnName.Replace("_", string.Empty);
        foreach (var property in type.GetProperties())
        {
            if (string.Equals(property.Name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        throw new InvalidOperationException(
            $"No property on {type.Name} matches column '{columnName}'. Every dialect's SELECT column must " +
            $"correspond to a ChallengeRow property once underscores are stripped.");
    }
}
