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
/// <see cref="IChallengeDialect.SelectWindowCountsSql"/> and <see cref="IChallengeDialect.IncrementWindowSql"/>
/// are both keyed by a single <c>window_start</c> value per bucket — a sliding window needs either a
/// rolling log of individual issuance timestamps (a third table) or an approximation, neither of which
/// this schema carries. The accepted tradeoff is bucket-boundary bursting: a caller can issue up to
/// <c>Limit</c> secrets in the last second of one bucket and <c>Limit</c> more in the first second of
/// the next, i.e. up to <c>2 * Limit</c> in a short span straddling the boundary. This is acceptable
/// here because the per-key ceiling exists to bound cost, not to stop brute force (see
/// <see cref="PurposeOptions.PerKeyWindow"/>'s remarks) — a fixed bucket's worst-case burst is still a
/// small, bounded multiple of the configured limit, not an unbounded one.
/// </para>
/// <para>
/// <b>Known limitation: <see cref="IChallengeDialect.SelectLiveByScopeSql"/> returns at most one row</b>
/// (every shipped dialect orders by <c>created_at DESC</c> and takes only the most recent), so
/// <see cref="VerifyAsync"/> can only ever compare against the most recently issued live challenge for a
/// scope. With the default <c>MaxLiveChallenges = 1</c> this is exactly correct — there is only ever one
/// live row. When a purpose configures <c>MaxLiveChallenges &gt; 1</c>, an older still-live challenge
/// that is not the most recent cannot be verified through this path even though it has not expired or
/// been invalidated; the dialect interface has no statement that returns more than one live row. The
/// re-issue policy below is written around the same constraint: it invalidates outstanding challenges
/// only when <see cref="PurposeOptions.MaxLiveChallenges"/> is 1 (the statement that would enforce a
/// higher cap — invalidate all but a chosen subset — does not exist; <see cref="IChallengeDialect.InvalidateLiveForScopeSql"/>
/// only supports invalidating every live row for a scope, not a subset of them), and relies on the
/// per-scope rate-limit window plus each challenge's own TTL to bound how many can accumulate.
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
        var keyWindowStart = FloorToWindowStart(now, purpose.PerKeyWindow.Window);

        await using var connection = dialect.CreateConnection();

        var counts = await connection.QueryAsync<WindowCountRow>(new CommandDefinition(
            dialect.SelectWindowCountsSql,
            new
            {
                TenantId = scope.TenantId,
                Key = scope.Key,
                Purpose = scope.Purpose,
                ScopeWindowStart = scopeWindowStart,
                KeyWindowStart = keyWindowStart,
            },
            cancellationToken: cancellationToken));

        var keyCount = 0;
        var scopeCount = 0;
        foreach (var row in counts)
        {
            if (row.Purpose is null)
            {
                keyCount = row.Count;
            }
            else
            {
                scopeCount = row.Count;
            }
        }

        // Per-key first: it is the cost ceiling across every purpose defined for this key — the layer
        // that protects the invoice. Per-scope is the narrower, UX-facing limit. Either refusing means
        // no secret is generated and no row is written; the caller pays nothing for a refused issue.
        if (keyCount >= purpose.PerKeyWindow.Limit || scopeCount >= purpose.PerScopeWindow.Limit)
        {
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

        // Both layers, always — see the type's remarks on the per-key ceiling being the cost layer.
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.IncrementWindowSql,
            new { Id = Guid.NewGuid(), TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose, WindowStart = scopeWindowStart },
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.IncrementWindowSql,
            new { Id = Guid.NewGuid(), TenantId = scope.TenantId, Key = scope.Key, Purpose = (string?)null, WindowStart = keyWindowStart },
            cancellationToken: cancellationToken));

        logger.LogInformation("Challenge issued for purpose {Purpose}", scope.Purpose);
        return ChallengeIssueResult.Issued(secret, expiresAt);
    }

    /// <inheritdoc />
    public async Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(code);

        var purpose = options.GetPurpose(scope.Purpose);
        var now = timeProvider.GetUtcNow();

        await using var connection = dialect.CreateConnection();

        var row = await connection.QueryFirstOrDefaultAsync<ChallengeRow>(new CommandDefinition(
            dialect.SelectLiveByScopeSql, LiveByScopeParams(scope, now), cancellationToken: cancellationToken));

        if (row is null)
        {
            var outcome = await ClassifyMissingAsync(connection, scope, cancellationToken);
            logger.LogInformation("Challenge verify {Outcome} for purpose {Purpose}", outcome.Outcome, scope.Purpose);
            return outcome;
        }

        if (SecretHasher.Verify(code, row.SecretHash, row.SecretSalt))
        {
            var consumedRows = await connection.ExecuteAsync(new CommandDefinition(
                dialect.ConsumeSql, new { row.Id, Now = now, ConsumedAt = now }, cancellationToken: cancellationToken));

            // Rows-affected 0 means someone else's concurrent VerifyAsync already consumed this exact
            // row between our SELECT and our UPDATE — they won the race. Reporting Verified here too
            // would let the same secret succeed twice.
            var outcome = consumedRows > 0 ? ChallengeVerifyResult.Verified(scope) : ChallengeVerifyResult.Consumed(scope);
            logger.LogInformation("Challenge verify {Outcome} for purpose {Purpose}", outcome.Outcome, scope.Purpose);
            return outcome;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.RecordAttemptSql, new { row.Id }, cancellationToken: cancellationToken));

        var attemptsAfter = row.Attempts + 1;
        var mismatchOutcome = attemptsAfter >= purpose.MaxAttempts
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
    public async Task RefundAsync(ChallengeScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var purpose = options.GetPurpose(scope.Purpose);
        var now = timeProvider.GetUtcNow();
        var scopeWindowStart = FloorToWindowStart(now, purpose.PerScopeWindow.Window);
        var keyWindowStart = FloorToWindowStart(now, purpose.PerKeyWindow.Window);

        await using var connection = dialect.CreateConnection();

        // Both layers, mirroring IssueAsync's two IncrementWindowSql calls — a refund undoes exactly
        // what an issue charged. Each is independently floored at zero by the dialect.
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.DecrementWindowSql,
            new { TenantId = scope.TenantId, Key = scope.Key, Purpose = scope.Purpose, WindowStart = scopeWindowStart },
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.DecrementWindowSql,
            new { TenantId = scope.TenantId, Key = scope.Key, Purpose = (string?)null, WindowStart = keyWindowStart },
            cancellationToken: cancellationToken));

        logger.LogInformation("Challenge quota refunded for purpose {Purpose}", scope.Purpose);
    }

    /// <summary>
    /// Distinguishes <see cref="ChallengeVerifyOutcome.Expired"/> from <see cref="ChallengeVerifyOutcome.NotFound"/>
    /// when the live lookup found nothing. <see cref="IChallengeDialect.SelectLiveByScopeSql"/>'s own
    /// <c>WHERE</c> clause filters both <c>consumed_at IS NULL</c> and <c>expires_at &gt; @Now</c>, and
    /// the interface has no statement that selects ignoring expiry — so this re-runs the exact same
    /// statement with <c>@Now</c> pinned to <see cref="DateTimeOffset.MinValue"/>. That value defeats
    /// only the expiry filter (every real <c>expires_at</c> is after year 1) while leaving the
    /// <c>consumed_at IS NULL</c> filter intact: a row still turns up here if and only if it was issued,
    /// is not consumed or invalidated, and its TTL has since elapsed.
    /// </summary>
    private async Task<ChallengeVerifyResult> ClassifyMissingAsync(
        System.Data.Common.DbConnection connection, ChallengeScope scope, CancellationToken cancellationToken)
    {
        var everLive = await connection.QueryFirstOrDefaultAsync<ChallengeRow>(new CommandDefinition(
            dialect.SelectLiveByScopeSql, LiveByScopeParams(scope, DateTimeOffset.MinValue), cancellationToken: cancellationToken));

        return everLive is null ? ChallengeVerifyResult.NotFound(scope) : ChallengeVerifyResult.Expired(scope);
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
