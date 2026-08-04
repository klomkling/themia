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

        // Charge every counter FIRST, then compare each against its limit using the value the increment
        // itself returned. Reading the counts and only then incrementing — the obvious shape, and the
        // one this originally had — is a read-then-act gate with nothing serializing it: 64 callers
        // racing for the same bucket all read the same pre-increment count, all find it under the
        // ceiling, and all issue. The counters would still be exact (no increment is lost) while the
        // limit they exist to enforce is simply not enforced, which is the failure mode an SMS bill
        // notices and a test asserting on the counter total does not. Charging first makes each
        // caller's observed value unique and monotonic, so at most Limit callers can ever see a value
        // at or below the ceiling, whatever the concurrency.
        //
        // `charges` records what has actually been charged so far. Every entry must be released if the
        // issuance does not end in a stored secret: a charge without a secret is quota burned for
        // nothing, and under the default 3-per-15-minutes per-scope window three transient database
        // failures leave a real user unable to request an OTP for the rest of the window despite never
        // having received one.
        var charges = new List<Charge>(3);
        int scopeCount;
        int keyCount;
        int? globalCount = null;

        try
        {
            scopeCount = await ChargeAsync(connection, charges, scope, scope.Purpose, scopeWindowStart, cancellationToken);
            keyCount = await ChargeAsync(connection, charges, scope, null, keyWindowStart, cancellationToken);

            // The tenant-agnostic ceiling, when configured. Bucketed by key alone — the scope's tenant
            // is dropped — because the SMS invoice and the victim's inbox are not partitioned by tenant
            // even though PerKeyWindow's counter is. See ChallengeOptions.PerKeyGlobalWindow.
            if (options.PerKeyGlobalWindow is { } globalWindow)
            {
                globalCount = await ChargeAsync(
                    connection,
                    charges,
                    scope with { TenantId = null },
                    ChallengeOptions.GlobalKeyBucketPurpose,
                    FloorToWindowStart(now, globalWindow.Window),
                    cancellationToken);
            }
        }
        catch
        {
            await ReleaseChargesAsync(connection, charges);
            throw;
        }

        // Every configured layer is required, not an alternative: per-key is the cost ceiling across
        // every purpose for this key, per-scope is the narrower UX-facing one, and the global layer —
        // when enabled — is the ceiling on the physical key regardless of which tenant asked.
        if (keyCount > options.PerKeyWindow.Limit
            || scopeCount > purpose.PerScopeWindow.Limit
            || (globalCount is { } g && options.PerKeyGlobalWindow is { } gw && g > gw.Limit))
        {
            // Hand every charge back: no secret was generated and no challenge row was written, so a
            // refused issue must not consume quota — otherwise a caller refused by the per-scope limit
            // would still burn the per-key ceiling, and repeated refusals would compound into a lockout
            // that outlasts the window that produced it. The release is best-effort by construction:
            // DecrementWindowSql floors at zero, so a concurrent caller can briefly observe this
            // caller's un-released +1 and be refused when it need not have been. That direction is the
            // safe one (it over-refuses, never over-admits) and it self-corrects within the round trip.
            await ReleaseChargesAsync(connection, charges);

            logger.LogInformation("Challenge issue rate-limited for purpose {Purpose}", scope.Purpose);
            return ChallengeIssueResult.RateLimited();
        }

        var challengeId = Guid.NewGuid();
        var secret = SecretGenerator.Generate(purpose.Format);
        var (hash, salt) = SecretHasher.Hash(secret);
        var expiresAt = now + purpose.Ttl;

        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            // The supersede-then-insert pair is atomic. Without the transaction, an insert that fails
            // after a successful invalidate leaves the user with their previous code already killed and
            // no new one — strictly worse than either operation not having run at all. The rate-limit
            // counters are deliberately NOT in this transaction: they are separate statements precisely
            // so concurrent callers serialize on their own row locks, and enrolling them here would
            // hold those locks for the whole issuance.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Re-issue policy: only MaxLiveChallenges == 1 is exactly enforceable with this dialect's
            // statements (see the type-level remarks) — invalidating on every issue is safe even when
            // nothing was previously live, since InvalidateLiveForScopeSql is a no-op UPDATE in that case.
            if (purpose.MaxLiveChallenges == 1)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    dialect.InvalidateLiveForScopeSql,
                    new { TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose, Now = now, ConsumedAt = now },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InsertSql,
                new
                {
                    Id = challengeId,
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
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await ReleaseChargesAsync(connection, charges);
            throw;
        }

        logger.LogInformation("Challenge issued for purpose {Purpose}", scope.Purpose);
        return ChallengeIssueResult.Issued(challengeId, secret, expiresAt);
    }

    /// <summary>One rate-limit bucket this issuance actually incremented, and therefore owes back if the
    /// issuance does not end in a stored secret.</summary>
    private readonly record struct Charge(ChallengeScope Scope, string? Purpose, DateTimeOffset WindowStart);

    /// <summary>
    /// Charges one bucket and records it in <paramref name="charges"/> before returning, so a failure in
    /// any later step knows exactly what to release. The record happens after the increment succeeds:
    /// a charge that threw may or may not have landed, and releasing one that never landed would credit
    /// quota nobody spent.
    /// </summary>
    private async Task<int> ChargeAsync(
        System.Data.Common.DbConnection connection,
        List<Charge> charges,
        ChallengeScope scope,
        string? purpose,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        var count = await IncrementWindowAsync(connection, scope, purpose, windowStart, cancellationToken);
        charges.Add(new Charge(scope, purpose, windowStart));
        return count;
    }

    /// <summary>
    /// Hands back every charge an issuance made that did not produce a secret. Runs with
    /// <see cref="CancellationToken.None"/> deliberately: this is compensation for work already done, and
    /// the most common reason to reach it is the caller's own token being cancelled — passing that token
    /// through would make the compensation fail exactly when it is needed. Failures are logged and
    /// swallowed so the original exception is what the caller sees; the counter self-heals when the
    /// window elapses.
    /// </summary>
    private async Task ReleaseChargesAsync(System.Data.Common.DbConnection connection, List<Charge> charges)
    {
        foreach (var charge in charges)
        {
            try
            {
                await DecrementWindowAsync(connection, charge.Scope, charge.Purpose, charge.WindowStart, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to release a rate-limit charge for purpose {Purpose}; the quota stays consumed until the window elapses",
                    charge.Scope.Purpose);
            }
        }
    }

    /// <summary>
    /// Charges one rate-limit bucket and returns its count <em>after</em> this increment. The returned
    /// value is the caller's own increment, produced by the same statement that wrote it — see
    /// <see cref="IChallengeDialect.IncrementWindowSql"/> for why a separate read would not be.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The statement returned no scalar. Deliberately fatal rather than defaulted: since the ceiling is
    /// now decided entirely by this value, a missing one silently read as 0 would be below every
    /// configured limit and admit the issuance unconditionally — the rate limiter would fail open, and
    /// do so invisibly. <c>ExecuteScalarAsync&lt;int&gt;</c> would have done exactly that, because Dapper
    /// maps a NULL/absent result to <c>default(int)</c>; MySQL's dialect can legitimately produce NULL
    /// (its statement resets the session variable first, precisely so a no-match UPDATE cannot return a
    /// previous call's value). Failing closed turns "the ceiling did not work" into a loud error.
    /// </exception>
    private async Task<int> IncrementWindowAsync(
        System.Data.Common.DbConnection connection,
        ChallengeScope scope,
        string? purpose,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            dialect.IncrementWindowSql,
            new { Id = Guid.NewGuid(), TenantId = scope.TenantId, Key = scope.Key, Purpose = purpose, WindowStart = windowStart },
            cancellationToken: cancellationToken));

        return count ?? throw new InvalidOperationException(
            "IncrementWindowSql returned no count. The rate-limit ceiling is decided by that value, so "
            + "the issuance is refused rather than admitted on an assumed count of zero. See the "
            + "contract on IChallengeDialect.IncrementWindowSql.");
    }

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
    public async Task<bool> RefundAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection();

        var row = await connection.QueryFirstOrDefaultAsync<ChallengeRow>(new CommandDefinition(
            dialect.SelectByIdSql, new { Id = challengeId }, cancellationToken: cancellationToken));

        if (row is null)
        {
            // Purged, or never issued. Not an error: retention deletes challenge rows long before the
            // counters they charged elapse, so a late webhook for a real issuance lands here routinely.
            logger.LogInformation("Challenge refund skipped: no challenge {ChallengeId}", challengeId);
            return false;
        }

        // The claim, and the whole reason a refund takes a challenge id rather than a scope and a
        // timestamp. Delivery-status webhooks are retried by every provider and adopters retry their own
        // failure handlers, so an unguarded decrement is refunded two or three times per failed send —
        // and since DecrementWindowSql floors at zero and never errors, anyone who can make deliveries
        // fail could replay it to drive the SMS cost ceiling to zero and keep issuing. Exactly one
        // caller wins this UPDATE; everyone else gets 0 rows and stops here.
        var claimed = await connection.ExecuteAsync(new CommandDefinition(
            dialect.MarkRefundedSql,
            new { Id = challengeId, Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));

        if (claimed == 0)
        {
            logger.LogInformation("Challenge refund skipped: challenge {ChallengeId} was already refunded", challengeId);
            return false;
        }

        // Buckets come from the row's own created_at, never from "now". They are fixed-width, so a
        // refund arriving after the boundary the issue fell on — the common case, since delivery failure
        // is discovered asynchronously — would otherwise decrement a bucket the issue never charged:
        // the original charge stays for the rest of its window, and an unrelated live bucket loses a
        // count belonging to somebody else's issuance. Both directions are wrong, and the second is a
        // quota bypass.
        var scope = new ChallengeScope(row.Key, row.Purpose, row.TenantId);
        var purpose = options.GetPurpose(row.Purpose);
        var scopeWindowStart = FloorToWindowStart(row.CreatedAt, purpose.PerScopeWindow.Window);
        var keyWindowStart = FloorToWindowStart(row.CreatedAt, options.PerKeyWindow.Window);

        // Every layer the issuance charged, mirroring IssueAsync — a refund undoes exactly what an issue
        // charged, no more and no less. Each is independently floored at zero by the dialect.
        await DecrementWindowAsync(connection, scope, scope.Purpose, scopeWindowStart, cancellationToken);
        await DecrementWindowAsync(connection, scope, null, keyWindowStart, cancellationToken);

        // The tenant-agnostic bucket, if that layer is configured. Read from the CURRENT options rather
        // than from anything stored: an issuance made while the layer was off has no such row, and
        // DecrementWindowSql is a no-op against a bucket that does not exist. The reverse — the layer
        // turned off between issue and refund — leaves that one counter uncredited until its window
        // elapses, which is the harmless direction.
        if (options.PerKeyGlobalWindow is { } globalWindow)
        {
            await DecrementWindowAsync(
                connection,
                scope with { TenantId = null },
                ChallengeOptions.GlobalKeyBucketPurpose,
                FloorToWindowStart(row.CreatedAt, globalWindow.Window),
                cancellationToken);
        }

        logger.LogInformation("Challenge quota refunded for purpose {Purpose}", scope.Purpose);
        return true;
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
