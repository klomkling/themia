using System.Data.Common;
using Npgsql;
using Themia.Challenges;

namespace Themia.Challenges.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IChallengeDialect"/> (Npgsql).</summary>
/// <remarks>
/// <c>tenant_id</c> is nullable on both <c>challenges</c> and <c>challenge_rate_windows</c>, and
/// <c>purpose</c> is additionally nullable on <c>challenge_rate_windows</c> (see
/// <see cref="Themia.Challenges.Migrations.ChallengeSchemaMigration"/>). Every predicate below that compares one of
/// those columns to a parameter that may itself be <see langword="null"/> uses
/// <c>IS NOT DISTINCT FROM</c> instead of <c>=</c> — plain <c>=</c> never matches a
/// <see langword="null"/> operand in SQL, so a platform-level challenge (<c>@TenantId</c> is
/// <see langword="null"/>) or the per-key ceiling window (<c>@Purpose</c> is <see langword="null"/>)
/// would silently match zero rows under <c>=</c>.
/// </remarks>
public sealed class PostgresChallengeDialect : IChallengeDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public PostgresChallengeDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO challenges (id, tenant_id, "key", purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at)
        VALUES (@Id, @TenantId, @Key, @Purpose, @SecretHash, @SecretSalt, @TokenHash, @Attempts, @ExpiresAt, @CreatedAt);
        """;

    /// <inheritdoc />
    /// <remarks>
    /// No <c>LIMIT</c> — returns every live row for the scope, not just the newest. See the interface's
    /// remarks: capping this to one row is exactly what would make <see cref="PurposeOptions.MaxLiveChallenges"/>
    /// values above 1 silently do nothing.
    /// </remarks>
    public string SelectLiveByScopeSql => """
        SELECT * FROM challenges
        WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC;
        """;

    /// <inheritdoc />
    public string SelectLiveByTokenHashSql => """
        SELECT * FROM challenges
        WHERE token_hash = @TokenHash AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC LIMIT 1;
        """;

    /// <inheritdoc />
    public string SelectMostRecentByScopeSql => """
        SELECT * FROM challenges
        WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key AND purpose = @Purpose
        ORDER BY created_at DESC LIMIT 1;
        """;

    /// <inheritdoc />
    public string SelectByIdSql => """SELECT * FROM challenges WHERE id = @Id;""";

    /// <inheritdoc />
    public string MarkRefundedSql => """
        UPDATE challenges SET refunded_at = @Now WHERE id = @Id AND refunded_at IS NULL;
        """;

    /// <inheritdoc />
    public string ConsumeSql => """
        UPDATE challenges SET consumed_at = @ConsumedAt
        WHERE id = @Id AND consumed_at IS NULL AND expires_at > @Now;
        """;

    /// <inheritdoc />
    public string RecordAttemptSql => """
        UPDATE challenges SET attempts = attempts + 1 WHERE id = @Id AND consumed_at IS NULL;
        """;

    /// <inheritdoc />
    public string InvalidateLiveForScopeSql => """
        UPDATE challenges SET consumed_at = @ConsumedAt
        WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now;
        """;

    /// <inheritdoc />
    public string PurgeExpiredSql => """DELETE FROM challenges WHERE expires_at < @OlderThan;""";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Not a single <c>INSERT ... ON CONFLICT (...) DO UPDATE</c>.</b> That form requires a
    /// conflict target — an exact column list plus, for a partial index, a <c>WHERE</c> predicate
    /// that textually matches the index's own predicate — and this table carries four different
    /// filtered unique indexes (see the remarks on <see cref="Themia.Challenges.Migrations.ChallengeSchemaMigration"/>),
    /// one per null-combination of <c>tenant_id</c>/<c>purpose</c>. Because <c>@TenantId</c> and
    /// <c>@Purpose</c> vary in nullability from call to call against this same static SQL text, no
    /// single static conflict target can name "whichever of the four indexes applies this time" —
    /// Postgres resolves the arbiter at parse time, not from the parameter values at execution time.
    /// Picking one predicate would make the statement correct for exactly one of the four
    /// combinations and throw <c>42P10</c> (no unique/exclusion constraint matching the target) for
    /// the other three.
    /// <para>
    /// Instead this is two statements sent as one batch, relying on <c>ON CONFLICT DO NOTHING</c>
    /// <i>without</i> a conflict target: unlike <c>DO UPDATE</c>, a target-less <c>DO NOTHING</c>
    /// suppresses a violation of <i>any</i> unique or exclusion constraint on the table, so it works
    /// uniformly across all four partial indexes. The insert seeds a fresh bucket at <c>count = 0</c>
    /// (or is silently skipped if a row already exists for this exact bucket); the following
    /// <c>UPDATE</c> then unconditionally adds 1 to whichever row now exists — the row this call just
    /// inserted, or the pre-existing one. Concurrent callers for the same brand-new bucket serialize
    /// on the unique index during <c>INSERT</c> (exactly one of them creates the row, the rest hit
    /// <c>DO NOTHING</c>) and then serialize again on the row lock during <c>UPDATE</c> (each sees the
    /// previous caller's committed count and adds 1), so no increment is ever lost.
    /// </para>
    /// <para>
    /// The <c>RETURNING count</c> on the <c>UPDATE</c> is what makes the limit enforceable rather than
    /// merely counted — see the contract on <see cref="IChallengeDialect.IncrementWindowSql"/>. It is
    /// the only statement in the batch that produces a result set (a target-less
    /// <c>ON CONFLICT DO NOTHING</c> insert produces none), so the caller reads it as a single scalar.
    /// </para>
    /// </remarks>
    public string IncrementWindowSql => """
        INSERT INTO challenge_rate_windows (id, tenant_id, "key", purpose, window_start, count)
        VALUES (@Id, @TenantId, @Key, @Purpose, @WindowStart, 0)
        ON CONFLICT DO NOTHING;
        UPDATE challenge_rate_windows SET count = count + 1
        WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key AND purpose IS NOT DISTINCT FROM @Purpose
          AND window_start = @WindowStart
        RETURNING count;
        """;

    /// <inheritdoc />
    public string DecrementWindowSql => """
        UPDATE challenge_rate_windows SET count = GREATEST(count - 1, 0)
        WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key AND purpose IS NOT DISTINCT FROM @Purpose
          AND window_start = @WindowStart;
        """;

    /// <inheritdoc />
    public string PurgeElapsedWindowsSql => """DELETE FROM challenge_rate_windows WHERE window_start < @OlderThan;""";
}
