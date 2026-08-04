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
    /// <remarks>
    /// No <c>TOP</c> — returns every live row for the scope, not just the newest. See the interface's
    /// remarks: capping this to one row is exactly what would make <see cref="PurposeOptions.MaxLiveChallenges"/>
    /// values above 1 silently do nothing.
    /// </remarks>
    public string SelectLiveByScopeSql => """
        SELECT * FROM challenges
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
    public string SelectMostRecentByScopeSql => """
        SELECT TOP (1) * FROM challenges
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key AND purpose = @Purpose
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
    /// <para>
    /// <b>Reading the new count back</b> uses <c>OUTPUT INSERTED.count</c> on the <c>UPDATE</c> — the
    /// post-image, emitted by the statement that holds the row lock, which is what the contract on
    /// <see cref="IChallengeDialect.IncrementWindowSql"/> requires and what makes the limit enforceable
    /// rather than merely counted. Nothing earlier in the batch produces a result set, so it is the
    /// caller's single scalar.
    /// </para>
    /// <para>
    /// <b>This is the only one of the three dialects whose upsert can raise for the benign
    /// collision.</b> PostgreSQL's <c>ON CONFLICT DO NOTHING</c> and MySQL's
    /// <c>ON DUPLICATE KEY UPDATE id = id</c> both suppress the duplicate at the engine level — no
    /// error is ever raised, so neither statement interacts with transaction-doom semantics at all.
    /// SQL Server has no such "insert, but silently no-op on collision" form, so this statement
    /// raises 2601/2627 and then <i>catches</i> it — which matters because of how <c>SET XACT_ABORT</c>
    /// changes what an error does to the ambient transaction. <c>System.Transactions.TransactionScope</c>
    /// sets <c>XACT_ABORT ON</c> automatically the moment a <see cref="SqlConnection"/> enlists in it —
    /// the obvious way an issuance flow (insert the challenge, then call this statement for both the
    /// per-scope and per-key windows) would be made atomic. Under <c>XACT_ABORT ON</c>, a run-time
    /// error dooms the <i>entire</i> transaction — confirmed empirically against a live SQL Server
    /// 2022 instance: with an ambient <c>XACT_ABORT ON</c> transaction and a colliding bucket, by the
    /// time the <c>CATCH</c> block runs, <c>XACT_STATE()</c> already reads <c>-1</c> (uncommittable),
    /// not <c>1</c> — the <c>THROW</c> filter never gets a live, committable transaction to return to,
    /// even though the filter itself still correctly identifies the error as benign.
    /// </para>
    /// <para>
    /// <b><c>SAVE TRANSACTION</c> / <c>ROLLBACK TRANSACTION</c> &lt;savepoint&gt; does not fix this —
    /// verified, not assumed.</b> The natural first fix is to wrap the seed <c>INSERT</c> in a
    /// savepoint and roll back only to it on the benign error, leaving the rest of the ambient
    /// transaction intact. Tested directly against a live SQL Server 2022 container: under an ambient
    /// <c>XACT_ABORT ON</c> transaction, a savepoint taken before the colliding <c>INSERT</c>, and a
    /// <c>ROLLBACK TRANSACTION</c> to that savepoint issued from the <c>CATCH</c> block once the error
    /// is confirmed benign — the rollback itself fails with <c>"The current transaction cannot be
    /// committed and cannot be rolled back to a savepoint. Roll back the entire transaction."</c>
    /// <c>XACT_ABORT ON</c> dooms the <i>whole</i> transaction on error, unconditionally, ignoring any
    /// savepoint taken earlier in it — a doomed transaction (<c>XACT_STATE() = -1</c>) can only be
    /// rolled back in full, never partially. This is a documented SQL Server interaction (see Erland
    /// Sommarskog's error-handling series), not specific to this schema, so no future revision of this
    /// statement should reach for <c>SAVE TRANSACTION</c> as the fix here.
    /// </para>
    /// <para>
    /// <b>What actually works: suppress the escalation for just the seed <c>INSERT</c>, not the
    /// symptom after the fact.</b> <c>SET XACT_ABORT OFF</c> immediately before the <c>INSERT</c>
    /// downgrades a duplicate-key violation back to the ordinary statement-level recoverable error it
    /// would be without <c>XACT_ABORT</c> — the exact behavior this statement already relied on before
    /// this fix, and the behavior every plain (non-<c>TransactionScope</c>) caller already gets by
    /// default. The original session setting is captured first via <c>@@OPTIONS &amp; 16384</c> (the
    /// documented <c>XACT_ABORT</c> bit — <c>SESSIONPROPERTY('XACT_ABORT')</c> looks like the obvious
    /// way to read it back but is <i>not</i> a valid option name for that function and silently returns
    /// <see langword="null"/>, confirmed against a live instance) and restored to exactly that value —
    /// not unconditionally forced back <c>ON</c> — both on the benign-collision path and immediately
    /// before <c>THROW</c> on a genuine error, so a caller who runs with <c>XACT_ABORT OFF</c> gets
    /// their setting back unchanged rather than having it silently flipped for the rest of their
    /// session. Verified against a live SQL Server 2022 instance across all three cases this remark
    /// describes: a colliding bucket inside an ambient <c>XACT_ABORT ON</c> transaction (the
    /// <c>TransactionScope</c> case) commits with the ambient transaction's other work intact and
    /// <c>XACT_STATE()</c> staying <c>1</c> throughout; the same collision under plain autocommit
    /// behaves exactly as before; and a genuine, unrelated error (a forced <c>NOT NULL</c> violation)
    /// still propagates via <c>THROW</c> and still dooms/rolls back the transaction — the fix narrows
    /// exactly the benign case, it does not widen what gets swallowed.
    /// </para>
    /// </remarks>
    public string IncrementWindowSql => """
        DECLARE @xactAbortWasOn bit = CASE WHEN @@OPTIONS & 16384 = 16384 THEN 1 ELSE 0 END;
        SET XACT_ABORT OFF;
        BEGIN TRY
            INSERT INTO challenge_rate_windows (id, tenant_id, [key], purpose, window_start, count)
            VALUES (@Id, @TenantId, @Key, @Purpose, @WindowStart, 0);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627)
            BEGIN
                IF @xactAbortWasOn = 1 SET XACT_ABORT ON;
                THROW;
            END
        END CATCH
        IF @xactAbortWasOn = 1 SET XACT_ABORT ON;
        UPDATE challenge_rate_windows SET count = count + 1
        OUTPUT INSERTED.count
        WHERE (tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL)) AND [key] = @Key
          AND (purpose = @Purpose OR (purpose IS NULL AND @Purpose IS NULL))
          AND window_start = @WindowStart;
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
