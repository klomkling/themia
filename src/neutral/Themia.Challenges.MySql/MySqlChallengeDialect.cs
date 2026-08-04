using System.Data.Common;
using MySqlConnector;
using Themia.Challenges;

namespace Themia.Challenges.MySql;

/// <summary>MySQL/MariaDB implementation of <see cref="IChallengeDialect"/> (MySqlConnector).</summary>
/// <remarks>
/// <para>
/// <b><c>key</c> is a reserved word on MySQL</b> (not on PostgreSQL): every statement below that
/// references the <c>key</c> column quotes it as <c>`key`</c>, per the type-level remarks on
/// <see cref="IChallengeDialect"/>.
/// </para>
/// <para>
/// <b>Null-safe comparison.</b> <c>tenant_id</c> is nullable on both tables, and <c>purpose</c> is
/// additionally nullable on <c>challenge_rate_windows</c> (see
/// <see cref="Themia.Challenges.Migrations.ChallengeSchemaMigration"/>). Every predicate below that
/// compares one of those columns to a parameter that may itself be <see langword="null"/> uses
/// MySQL's null-safe equal operator <c>&lt;=&gt;</c> instead of <c>=</c> — plain <c>=</c> never
/// matches a <see langword="null"/> operand, so a platform-level challenge (<c>@TenantId</c> is
/// <see langword="null"/>) or the per-key ceiling window (<c>@Purpose</c> is <see langword="null"/>)
/// would silently match zero rows under <c>=</c>. <c>purpose</c> on <c>challenges</c> is
/// <c>NOT NULL</c>, so plain <c>=</c> is correct there.
/// </para>
/// <para>
/// <b>MySQL treats schema and database as the same concept.</b> Unlike PostgreSQL/SQL Server,
/// there is no separate schema qualifier to get wrong here — <see cref="Themia.Challenges.Migrations.ChallengeSchemaMigration"/>
/// already accounts for this by using unqualified table names on every engine (see its type-level
/// remarks), so this dialect's statements need no schema prefix and none is added.
/// </para>
/// </remarks>
public sealed class MySqlChallengeDialect : IChallengeDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public MySqlChallengeDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO challenges (id, tenant_id, `key`, purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at)
        VALUES (@Id, @TenantId, @Key, @Purpose, @SecretHash, @SecretSalt, @TokenHash, @Attempts, @ExpiresAt, @CreatedAt);
        """;

    /// <inheritdoc />
    public string SelectLiveByScopeSql => """
        SELECT * FROM challenges
        WHERE tenant_id <=> @TenantId AND `key` = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC LIMIT 1;
        """;

    /// <inheritdoc />
    public string SelectLiveByTokenHashSql => """
        SELECT * FROM challenges
        WHERE token_hash = @TokenHash AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC LIMIT 1;
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
        WHERE tenant_id <=> @TenantId AND `key` = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now;
        """;

    /// <inheritdoc />
    public string PurgeExpiredSql => """DELETE FROM challenges WHERE expires_at < @OlderThan;""";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Not a single <c>INSERT ... ON DUPLICATE KEY UPDATE count = count + 1</c>.</b> MySQL enforces
    /// uniqueness on functional key parts exactly like an ordinary unique index — a plain
    /// <c>INSERT</c> whose column values fold, via the <c>IF(...)</c> expressions in
    /// <c>Themia.Challenges.Migrations.ChallengeSchemaMigration.CreateMySqlRateWindowUniqueIndexes</c>, to the same computed
    /// key as an existing row genuinely raises <c>ER_DUP_ENTRY</c> — so <c>ON DUPLICATE KEY UPDATE</c>
    /// would fire correctly against these functional indexes; that part of the trap this remark exists
    /// to warn about does not apply here. The reason this is still two statements is different: a
    /// single-statement <c>INSERT ... VALUES (...) ON DUPLICATE KEY UPDATE ...</c> has no <c>WHERE</c>
    /// clause at all — which row it updates is decided implicitly by whichever functional index the
    /// inserted values collide with, not by an explicit predicate this file can show. That leaves the
    /// null-safe <c>tenant_id</c>/<c>purpose</c> handling this type's remarks require entirely
    /// undocumented in the SQL text, unauditable, and untestable by
    /// <c>ChallengeDialectContractTests.TenantId_ShouldUseNullSafeComparison</c> /
    /// <c>Purpose_ShouldUseNullSafeComparison_OnIncrementAndDecrementWindow</c> — both of which assert
    /// against this member's SQL text and would pass vacuously (finding nothing to check) against a
    /// bare <c>ON DUPLICATE KEY UPDATE</c> form. So, matching <c>PostgresChallengeDialect</c>'s shape:
    /// <c>INSERT IGNORE</c> seeds a fresh bucket at <c>count = 0</c> (or is silently skipped — MySQL's
    /// <c>IGNORE</c> modifier suppresses <c>ER_DUP_ENTRY</c> for a violation of <i>any</i> unique index
    /// on the table, so it works uniformly across all four functional indexes, the same way Postgres's
    /// target-less <c>ON CONFLICT DO NOTHING</c> does); the following <c>UPDATE</c>, carrying the
    /// explicit null-safe predicate, then unconditionally adds 1 to whichever row now exists. Concurrent
    /// callers for the same brand-new bucket serialize on the functional unique index during
    /// <c>INSERT IGNORE</c> (exactly one of them creates the row) and then serialize again on the row
    /// lock during <c>UPDATE</c> (each sees the previous caller's committed count and adds 1), so no
    /// increment is ever lost and a new bucket always lands on 1, an existing one on n+1.
    /// </remarks>
    public string IncrementWindowSql => """
        INSERT IGNORE INTO challenge_rate_windows (id, tenant_id, `key`, purpose, window_start, count)
        VALUES (@Id, @TenantId, @Key, @Purpose, @WindowStart, 0);
        UPDATE challenge_rate_windows SET count = count + 1
        WHERE tenant_id <=> @TenantId AND `key` = @Key AND purpose <=> @Purpose
          AND window_start = @WindowStart;
        """;

    /// <inheritdoc />
    public string SelectWindowCountsSql => """
        SELECT purpose, count FROM challenge_rate_windows
        WHERE tenant_id <=> @TenantId AND `key` = @Key
          AND ((purpose = @Purpose AND window_start = @ScopeWindowStart) OR (purpose IS NULL AND window_start = @KeyWindowStart));
        """;

    /// <inheritdoc />
    public string DecrementWindowSql => """
        UPDATE challenge_rate_windows SET count = GREATEST(count - 1, 0)
        WHERE tenant_id <=> @TenantId AND `key` = @Key AND purpose <=> @Purpose
          AND window_start = @WindowStart;
        """;

    /// <inheritdoc />
    public string PurgeElapsedWindowsSql => """DELETE FROM challenge_rate_windows WHERE window_start < @OlderThan;""";
}
