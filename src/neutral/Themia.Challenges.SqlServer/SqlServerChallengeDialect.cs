using System.Data.Common;
using Microsoft.Data.SqlClient;
using Themia.Challenges;

namespace Themia.Challenges.SqlServer;

/// <summary>SQL Server implementation of <see cref="IChallengeDialect"/> (Microsoft.Data.SqlClient).</summary>
/// <remarks>
/// <para>
/// <b><c>key</c> is a reserved word on SQL Server</b> (not on PostgreSQL): every statement below that
/// references the <c>key</c> column quotes it as <c>[key]</c>, per the type-level remarks on
/// <see cref="IChallengeDialect"/>.
/// </para>
/// <para>
/// <b>Null-safe comparison.</b> SQL Server has no null-safe equality operator — neither PostgreSQL's
/// <c>IS NOT DISTINCT FROM</c> nor MySQL's <c>&lt;=&gt;</c> exist here. Every predicate below that
/// compares <c>tenant_id</c> or <c>purpose</c> to a parameter that may itself be
/// <see langword="null"/> instead uses the guard form
/// <c>(column = @Param OR (column IS NULL AND @Param IS NULL))</c> — plain <c>=</c> never matches a
/// <see langword="null"/> operand, so a platform-level challenge (<c>@TenantId</c> is
/// <see langword="null"/>) or the per-key ceiling window (<c>@Purpose</c> is <see langword="null"/>)
/// would silently match zero rows under <c>=</c>. <c>purpose</c> on <c>challenges</c> is
/// <c>NOT NULL</c>, so plain <c>=</c> is correct there.
/// </para>
/// </remarks>
public sealed class SqlServerChallengeDialect : IChallengeDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public SqlServerChallengeDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO challenges (id, tenant_id, [key], purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at)
        VALUES (@Id, @TenantId, @Key, @Purpose, @SecretHash, @SecretSalt, @TokenHash, @Attempts, @ExpiresAt, @CreatedAt);
        """;

    /// <inheritdoc />
    public string SelectLiveByScopeSql => """
        SELECT TOP (1) * FROM challenges
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC;
        """;

    /// <inheritdoc />
    public string SelectLiveByTokenHashSql => """
        SELECT TOP (1) * FROM challenges
        WHERE token_hash = @TokenHash AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC;
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
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now;
        """;

    /// <inheritdoc />
    public string PurgeExpiredSql => """DELETE FROM challenges WHERE expires_at < @OlderThan;""";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Not a single <c>MERGE</c>.</b> A bare <c>MERGE</c> is the single most common SQL Server
    /// upsert bug: under concurrent inserts for the same brand-new bucket, two sessions can both
    /// evaluate the <c>WHEN NOT MATCHED</c> branch under READ COMMITTED before either commits, and
    /// both attempt the <c>INSERT</c> — the second one dies on the unique index instead of falling
    /// through to an update. The textbook fix is <c>MERGE ... WITH (HOLDLOCK)</c>, which escalates the
    /// target-range read to serializable so the second session blocks instead of racing.
    /// <para>
    /// <c>MERGE WITH (HOLDLOCK)</c> would have worked here — unlike PostgreSQL's <c>ON CONFLICT</c>,
    /// <c>MERGE</c>'s <c>ON</c> clause is an ordinary boolean predicate, not a declared conflict
    /// target, so it does not share Postgres's problem of needing to name one of the four filtered
    /// unique indexes (see <c>ChallengeSchemaMigration.CreateRateWindowUniqueIndexes</c>) up front —
    /// the null-safe guard could sit directly in the <c>ON</c> clause and match whichever index
    /// applies. It was rejected anyway, for two reasons independent of that specific trap:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Consistency with the other two dialects.</b> PostgreSQL and MySQL both landed on the same
    /// two-statement shape — insert-if-absent seeding <c>count = 0</c>, then an unconditional
    /// <c>UPDATE ... count = count + 1</c> — for their own engine-specific reasons (documented on
    /// <c>PostgresChallengeDialect.IncrementWindowSql</c> and <c>MySqlChallengeDialect.IncrementWindowSql</c>
    /// in the sibling dialect packages). A third dialect that instead reaches for the one T-SQL-only
    /// upsert construct makes the three engines
    /// harder to compare side by side for the one caller (<c>Themia.Challenges</c>'s issuance path)
    /// that has to trust all three behave identically.
    /// </description></item>
    /// <item><description>
    /// <b><c>MERGE</c> has a wider track record of edge-case bugs than just this race</b> — Microsoft's
    /// own connect/feedback history documents <c>MERGE</c> misbehaving under snapshot isolation,
    /// firing triggers unexpected numbers of times, and producing wrong results against indexed
    /// views, on top of the well-known concurrent-insert race this remark opened with. Even with
    /// <c>HOLDLOCK</c> applied, that is a bigger surface to have to reason about than a plain
    /// <c>INSERT</c> guarded by a unique index and a <c>TRY/CATCH</c> — a pattern whose correctness
    /// rests entirely on ordinary unique-index enforcement, the same primitive PostgreSQL's
    /// <c>ON CONFLICT DO NOTHING</c> and MySQL's <c>ON DUPLICATE KEY UPDATE</c> both rest on.
    /// </description></item>
    /// </list>
    /// <para>
    /// So this is two statements, exactly like the other two dialects: the seed <c>INSERT</c> is
    /// wrapped in <c>BEGIN TRY/BEGIN CATCH</c> and re-throws anything that is not a duplicate-key
    /// violation (error <c>2601</c>, "duplicate key row" on a non-primary unique index — the four
    /// filtered indexes this table relies on — or <c>2627</c>, the primary-key/unique-constraint
    /// form, kept defensively even though every seeded <c>id</c> is a fresh GUID). A genuine
    /// duplicate-key violation here is expected and benign — some other session already created this
    /// bucket — so it is swallowed exactly the way <c>ON CONFLICT DO NOTHING</c> and
    /// <c>ON DUPLICATE KEY UPDATE id = id</c> swallow it on the other two engines; anything else
    /// (a constraint violation unrelated to the race, a truncation error, and so on) is rethrown
    /// unchanged via <c>THROW</c>, preserving the original error rather than masking it. The
    /// following <c>UPDATE</c>, carrying the explicit null-safe predicate, then unconditionally adds 1
    /// to whichever row now exists. Concurrent callers for the same brand-new bucket serialize on the
    /// unique index during the seed <c>INSERT</c> (exactly one of them creates the row; SQL Server's
    /// row-level locking blocks the second insert attempt until the first commits, then reports it as
    /// a duplicate) and then serialize again on the row lock during <c>UPDATE</c> (each sees the
    /// previous caller's committed count and adds 1), so no increment is ever lost and a new bucket
    /// always lands on 1, an existing one on n+1, two concurrent callers on 2 — never both on 1.
    /// </para>
    /// </remarks>
    public string IncrementWindowSql => """
        BEGIN TRY
            INSERT INTO challenge_rate_windows (id, tenant_id, [key], purpose, window_start, count)
            VALUES (@Id, @TenantId, @Key, @Purpose, @WindowStart, 0);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
        END CATCH
        UPDATE challenge_rate_windows SET count = count + 1
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key
          AND (purpose = @Purpose OR (purpose IS NULL AND @Purpose IS NULL))
          AND window_start = @WindowStart;
        """;

    /// <inheritdoc />
    public string SelectWindowCountsSql => """
        SELECT purpose, count FROM challenge_rate_windows
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key
          AND ((purpose = @Purpose AND window_start = @ScopeWindowStart) OR (purpose IS NULL AND window_start = @KeyWindowStart));
        """;

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server's <c>GREATEST</c>/<c>LEAST</c> functions require compatibility level 160 (SQL
    /// Server 2022+), which not every adopter's instance runs at. The portable
    /// <c>CASE WHEN ... THEN 0 ELSE ... END</c> form below floors the same way on every supported
    /// SQL Server version, matching PostgreSQL's <c>GREATEST(count - 1, 0)</c> and MySQL's
    /// <c>GREATEST(count - 1, 0)</c> in effect, not in syntax.
    /// </remarks>
    public string DecrementWindowSql => """
        UPDATE challenge_rate_windows SET count = CASE WHEN count - 1 < 0 THEN 0 ELSE count - 1 END
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key
          AND (purpose = @Purpose OR (purpose IS NULL AND @Purpose IS NULL))
          AND window_start = @WindowStart;
        """;

    /// <inheritdoc />
    public string PurgeElapsedWindowsSql => """DELETE FROM challenge_rate_windows WHERE window_start < @OlderThan;""";
}
