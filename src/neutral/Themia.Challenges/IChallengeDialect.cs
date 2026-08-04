using System.Data.Common;

namespace Themia.Challenges;

/// <summary>
/// Per-database strategy for the challenge store: supplies a connection and every SQL statement the
/// engine runs against the <c>challenges</c> and <c>challenge_rate_windows</c> tables created by
/// <see cref="Migrations.ChallengeSchemaMigration"/>. Implemented once per provider package
/// (PostgreSql/MySql/SqlServer) — this interface is the seam those packages are written against, and
/// their only contact with this assembly.
/// </summary>
/// <remarks>
/// Every statement is Dapper-executed with named parameters; the parameter names documented on each
/// member are the contract an implementation must bind, not a suggestion. None of these statements
/// enforce tenant isolation by construction — the caller always supplies <c>@TenantId</c> (which may be
/// <see langword="null"/> for a platform-level challenge) and every statement scopes its predicate to
/// exactly the value it was given.
/// <para>
/// <b><c>key</c> is a reserved word on MySQL and SQL Server</b> (not on PostgreSQL) — every statement
/// below that references the <c>key</c> column must quote it per engine (<c>"key"</c> on PostgreSQL,
/// <c>`key`</c> on MySQL, <c>[key]</c> on SQL Server) or the statement fails to parse on two of the
/// three. See <c>Themia.Modules.Storage.Migrations.StorageSchemaMigration</c> and
/// <c>Themia.Modules.Pdf.Migrations.PdfTemplateSchemaMigration</c> for the same rule applied to their
/// own <c>key</c> columns.
/// </para>
/// <para>
/// <b><c>tenant_id</c> (both tables) and <c>purpose</c> (<c>challenge_rate_windows</c> only) are
/// nullable</b> — <c>null</c> means a platform-level challenge (no tenant) or the per-key ceiling row
/// (every purpose, not one), respectively. Every predicate comparing one of these columns to a
/// parameter that may itself be <see langword="null"/> (<c>@TenantId</c> throughout;
/// <c>@Purpose</c> only where the member's docs say it may be <see langword="null"/>) MUST use a
/// null-safe comparison, never plain <c>=</c>. Ordinary SQL <c>=</c> never matches a <c>NULL</c>
/// operand, so <c>tenant_id = @TenantId</c> with <c>@TenantId = NULL</c> silently matches zero rows
/// instead of the platform-level rows it was meant to find — <b>this does not error</b>, it just
/// makes every platform-level challenge and every per-key ceiling row invisible to every query that
/// gets this wrong. The per-key ceiling is the layer that bounds the SMS bill, so getting this wrong
/// there disables that protection without any visible failure. Per-engine null-safe forms:
/// <list type="bullet">
/// <item><description>PostgreSQL — <c>IS NOT DISTINCT FROM</c> (e.g. <c>tenant_id IS NOT DISTINCT FROM @TenantId</c>).</description></item>
/// <item><description>MySQL — <c>&lt;=&gt;</c>, the null-safe equal operator (e.g. <c>tenant_id &lt;=&gt; @TenantId</c>).</description></item>
/// <item><description>SQL Server — no operator exists; use <c>(tenant_id = @TenantId OR (tenant_id IS NULL AND @TenantId IS NULL))</c>,
/// or the equivalent <c>EXISTS (SELECT tenant_id INTERSECT SELECT @TenantId)</c> form.</description></item>
/// </list>
/// <c>purpose</c> on the <c>challenges</c> table is <c>NOT NULL</c>, so plain <c>=</c> is correct
/// there and on any predicate where the doc states the parameter is always a concrete, non-null
/// value (e.g. the per-scope leg of <see cref="SelectWindowCountsSql"/>'s <c>@Purpose</c>) — null-safe
/// comparison is only required where the column is nullable AND the parameter may legitimately be
/// null for that call.
/// </para>
/// </remarks>
public interface IChallengeDialect
{
    /// <summary>Creates a new, unopened connection to the challenge store.</summary>
    /// <returns>A provider-specific <see cref="DbConnection"/> targeting the configured database.</returns>
    DbConnection CreateConnection();

    /// <summary>
    /// Inserts a newly issued challenge row. Params: <c>@Id</c>, <c>@TenantId</c>, <c>@Key</c>,
    /// <c>@Purpose</c>, <c>@SecretHash</c>, <c>@SecretSalt</c>, <c>@TokenHash</c>, <c>@Attempts</c>
    /// (always 0 at issuance), <c>@ExpiresAt</c>, <c>@CreatedAt</c>. <c>consumed_at</c> is not bound —
    /// a freshly issued row is always unconsumed, so the column is left at its natural
    /// <see langword="null"/> rather than passed explicitly.
    /// </summary>
    string InsertSql { get; }

    /// <summary>
    /// Selects <b>every</b> live challenge for a scope — every row matching <c>@TenantId</c>,
    /// <c>@Key</c>, <c>@Purpose</c> whose <c>consumed_at IS NULL</c> and <c>expires_at &gt; @Now</c>,
    /// ordered <c>created_at DESC</c>. Params: <c>@TenantId</c>, <c>@Key</c>, <c>@Purpose</c>,
    /// <c>@Now</c>.
    /// <para>
    /// <b>Must not cap the result to one row.</b> Under the default <c>MaxLiveChallenges = 1</c> at
    /// most one row will match, so the distinction is invisible there — but
    /// <see cref="PurposeOptions.MaxLiveChallenges"/> exists specifically so a purpose can keep more
    /// than one challenge live at once (see its remarks: a late-arriving first SMS must still verify
    /// after a resend). A caller has no way to verify against an older still-live challenge if this
    /// statement only ever returns the newest one — raising <c>MaxLiveChallenges</c> would then change
    /// nothing observable, silently defeating the one thing it exists to do. The <c>ORDER BY</c> is
    /// still required (newest first) so a caller that only wants the most recent — e.g. a re-issue
    /// policy checking whether anything is currently live — doesn't have to re-sort; the caller decides
    /// how many rows to consume, not this statement.
    /// </para>
    /// </summary>
    string SelectLiveByScopeSql { get; }

    /// <summary>
    /// Selects the live challenge by its token hash — the row matching <c>@TokenHash</c> whose
    /// <c>consumed_at IS NULL</c> and <c>expires_at &gt; @Now</c>. Params: <c>@TokenHash</c>,
    /// <c>@Now</c>. Serves the magic-link / email-verification path, where the caller has only the
    /// token and not the original key and purpose. Same tie-break as
    /// <see cref="SelectLiveByScopeSql"/> if more than one row matches.
    /// </summary>
    string SelectLiveByTokenHashSql { get; }

    /// <summary>
    /// Atomically consumes one challenge by id: sets <c>consumed_at = @ConsumedAt</c> for the row
    /// matching <c>@Id</c> where <c>consumed_at IS NULL AND expires_at &gt; @Now</c>. Params:
    /// <c>@Id</c>, <c>@Now</c>, <c>@ConsumedAt</c>.
    /// <para>
    /// This MUST be a single conditional <c>UPDATE</c> that carries its own guard, never a
    /// <c>SELECT</c> followed by a separate <c>UPDATE</c>. The guard is what makes verification
    /// atomic: when two concurrent callers race to consume the same row, exactly one <c>UPDATE</c>
    /// affects a row (returns 1) and every other concurrent call affects none (returns 0) — the
    /// database's row-level locking arbitrates the race, not application code. A caller that reads
    /// the row first and then writes introduces a window in which two concurrent verifications can
    /// both observe "still live" and both report success, defeating single-use consumption. Rows
    /// affected is the entire result the caller needs: 1 means this call won, 0 means it lost
    /// (already consumed, expired, or the id does not exist) — the caller does not need to
    /// distinguish those three cases from this statement alone, since a prior read (or
    /// <see cref="SelectLiveByScopeSql"/> / <see cref="SelectLiveByTokenHashSql"/>) already
    /// established which applies.
    /// </para>
    /// </summary>
    string ConsumeSql { get; }

    /// <summary>
    /// Records one incorrect verify attempt: increments <c>attempts</c> by 1 for the row matching
    /// <c>@Id</c> where <c>consumed_at IS NULL</c>. Params: <c>@Id</c>. Returns rows affected (0 if
    /// the row was consumed or removed between the failed comparison and this call).
    /// </summary>
    string RecordAttemptSql { get; }

    /// <summary>
    /// Marks every still-live challenge for a scope as no longer live: sets
    /// <c>consumed_at = @ConsumedAt</c> for every row matching <c>@TenantId</c>, <c>@Key</c>,
    /// <c>@Purpose</c> where <c>consumed_at IS NULL AND expires_at &gt; @Now</c>. Params:
    /// <c>@TenantId</c>, <c>@Key</c>, <c>@Purpose</c>, <c>@Now</c>, <c>@ConsumedAt</c>. Used by the
    /// re-issue policy to supersede outstanding challenges once <c>MaxLiveChallenges</c> is reached —
    /// it reuses <c>consumed_at</c> rather than a separate flag because every liveness predicate in
    /// this interface already keys off <c>consumed_at IS NULL</c>.
    /// </summary>
    string InvalidateLiveForScopeSql { get; }

    /// <summary>
    /// Hard-deletes challenge rows whose <c>expires_at &lt; @OlderThan</c>. Params: <c>@OlderThan</c>.
    /// Returns rows affected. Challenges are purged aggressively (every login attempt creates one) —
    /// this is a real delete, not a soft delete, because nothing downstream reads a dead challenge
    /// row. Contrast <see cref="PurgeElapsedWindowsSql"/>, which purges on a much longer horizon: the
    /// rate-limit counters must outlive the challenges they counted, or an attacker simply waits for
    /// this purge to run and the cost ceiling resets itself.
    /// </summary>
    string PurgeExpiredSql { get; }

    /// <summary>
    /// Atomically increments the rate-limit counter for one window bucket: the row identified by
    /// <c>@TenantId</c>, <c>@Key</c>, <c>@Purpose</c>, <c>@WindowStart</c>. Params: <c>@Id</c> (a
    /// freshly generated id, used only if no row exists yet), <c>@TenantId</c>, <c>@Key</c>,
    /// <c>@Purpose</c>, <c>@WindowStart</c>. If no row matches the bucket, inserts one with
    /// <c>count = 1</c>; otherwise increments the existing row's <c>count</c> by 1 — an upsert,
    /// implemented with whatever atomic construct the engine offers (<c>ON CONFLICT</c>,
    /// <c>INSERT ... ON DUPLICATE KEY UPDATE</c>, <c>MERGE</c>) so concurrent issuances for the same
    /// bucket never lose an increment.
    /// <para>
    /// <c>@Purpose</c> is <see langword="null"/> for the per-key ceiling row (the layer that bounds
    /// an SMS bill across every purpose defined for a key) and the actual purpose string for the
    /// per-scope row (the UX-facing limit for one purpose). Issuing one challenge calls this
    /// statement twice — once per layer — because both rate-limit layers are required, not
    /// alternatives.
    /// </para>
    /// <para>
    /// <c>@WindowStart</c> is a fixed-width bucket boundary the caller computes by flooring the
    /// current time to the purpose's configured window duration; this statement does not decide
    /// bucket width, it only targets the bucket it is given.
    /// </para>
    /// </summary>
    string IncrementWindowSql { get; }

    /// <summary>
    /// Reads the current counts for both rate-limit layers of one issuance check in a single
    /// round trip: the per-scope window (<c>purpose = @Purpose</c>, <c>window_start = @ScopeWindowStart</c>)
    /// and the per-key ceiling window (<c>purpose IS NULL</c>, <c>window_start = @KeyWindowStart</c>).
    /// Params: <c>@TenantId</c>, <c>@Key</c>, <c>@Purpose</c>, <c>@ScopeWindowStart</c>,
    /// <c>@KeyWindowStart</c>. Returns zero, one, or two rows, each carrying the row's nullable
    /// <c>purpose</c> and its <c>count</c>; the caller distinguishes which layer a row answers by
    /// whether its <c>purpose</c> is <see langword="null"/>. A layer with no matching row has not
    /// been charged yet in the current window and its count is 0.
    /// </summary>
    string SelectWindowCountsSql { get; }

    /// <summary>
    /// Atomically decrements the rate-limit counter for one window bucket — the refund path: the row
    /// identified by <c>@TenantId</c>, <c>@Key</c>, <c>@Purpose</c>, <c>@WindowStart</c> has its
    /// <c>count</c> reduced by 1, floored at 0 (never negative). Params: <c>@TenantId</c>,
    /// <c>@Key</c>, <c>@Purpose</c>, <c>@WindowStart</c>. Mirrors <see cref="IncrementWindowSql"/>:
    /// called once per layer, with the same <c>@Purpose</c>/<see langword="null"/> convention, and
    /// with the same <c>@WindowStart</c> value that the original <see cref="IncrementWindowSql"/>
    /// call for that issuance used. A message that was never delivered must not consume the victim's
    /// quota — this is what a caller invokes when delivery is known to have failed. If the bucket has
    /// already been purged by <see cref="PurgeElapsedWindowsSql"/> (the window elapsed before the
    /// refund arrived), no row matches and this is a no-op rather than an error.
    /// </summary>
    string DecrementWindowSql { get; }

    /// <summary>
    /// Hard-deletes counter rows whose <c>window_start &lt; @OlderThan</c>. Params: <c>@OlderThan</c>.
    /// Returns rows affected. The caller computes <c>@OlderThan</c> from the longest window duration
    /// configured across every purpose's <c>PerScopeWindow</c>/<c>PerKeyWindow</c>, plus a safety
    /// margin — never from a fixed retention shorter than a configured window. A counter row has no
    /// stored duration of its own, so purging it too early deletes evidence a still-active window
    /// depends on, silently resetting the cost ceiling the two-table split exists to protect; a
    /// counter must outlive the challenges it counted.
    /// </summary>
    string PurgeElapsedWindowsSql { get; }
}
