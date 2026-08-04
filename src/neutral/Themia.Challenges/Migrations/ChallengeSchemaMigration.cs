using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Challenges.Migrations;

/// <summary>Creates the <c>challenges</c> and <c>challenge_rate_windows</c> tables on PostgreSQL,
/// MySQL/MariaDB, and SQL Server. FluentMigrator is the single DDL authority for both the EF and
/// Dapper data layers (DECISION #6).</summary>
/// <remarks>
/// <para>
/// <b>Two tables, deliberately — the split is load-bearing.</b> An earlier design kept the
/// rate-limit count inside the challenge row; that is wrong because the two have opposite lifetimes.
/// <c>challenges</c> must be purged aggressively — every login attempt creates one, short-TTL,
/// single-use, and nothing downstream reads a dead row — but purging <c>challenge_rate_windows</c> on
/// the same schedule would erase the evidence the rate limiter counts from: an attacker who knows a
/// victim's key simply waits for that retention window to pass and the ceiling that bounds an SMS
/// bill resets itself for free. A counter must outlive the challenges it counted, so the two live in
/// separate tables purged on separate, independently-tunable horizons.
/// </para>
/// <para>
/// <b>Unprefixed literal table names on every engine, never <c>InSchema(...)</c>.</b> FluentMigrator
/// drops <c>InSchema(...)</c> on MySQL — there, "schema" and "database" are the same concept, and the
/// migration runs against whatever database the connection string already selects — so a
/// schema-qualified name means something different per engine. That divergence is exactly how
/// <c>Themia.Modules.Messaging</c>'s <c>MessagingSchemaMigration</c> ended up with its
/// <c>outbox_messages</c> once colliding with <c>Themia.Modules.Notifications</c>'s
/// identically-named table on MySQL. One literal table name on every engine removes the class of
/// defect instead of patching one instance of it.
/// </para>
/// <para>
/// <b><c>key</c> is 450 characters, not longer.</b> SQL Server caps an indexed <c>nvarchar</c> column
/// at 450 bytes (the 900-byte index key-size limit halved for Unicode); a wider column would silently
/// fail to build the <c>(tenant_id, key, purpose)</c> index there instead of erroring loudly. 450 is
/// the ceiling every engine can index, so all three use it rather than diverging per provider.
/// </para>
/// <para>
/// Indexes are deliberately plain, not unique: <c>PurposeOptions.MaxLiveChallenges</c> can be
/// configured above its default of 1, which legitimately allows more than one live row for the same
/// <c>(tenant_id, key, purpose)</c> at once. A unique constraint — even one filtered to live rows —
/// would reject that configuration outright, so uniqueness is enforced by the engine's re-issue
/// policy (<see cref="IChallengeDialect.InvalidateLiveForScopeSql"/>), not by the schema.
/// </para>
/// </remarks>
[Migration(202608040001, "Themia.Challenges: create challenges and challenge_rate_windows")]
public sealed class ChallengeSchemaMigration : Migration
{
    private const string ChallengesTable = "challenges";
    private const string RateWindowsTable = "challenge_rate_windows";

    /// <summary>Maps a datetime column to the engine-appropriate type. MySQL's FluentMigrator
    /// generator does not support <c>DateTimeOffset</c>, so MySQL uses <c>DATETIME(6)</c> while
    /// PostgreSQL and SQL Server use <c>datetimeoffset</c> / <c>timestamptz</c>, preserving timezone
    /// fidelity for the expiry and window-boundary columns.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this per-provider list and the unsupported-provider guard below are two parallel
        // whitelists that MUST agree. Adding a provider here without adding its prefix to the guard
        // leaves it throwing NotSupportedException; adding it to the guard without a branch here lets
        // it through to a column-type failure. Edit BOTH when adding a provider.
        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Challenges supports only PostgreSQL, MySQL/MariaDB, and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    private void CreateTables(DateTimeType dt)
    {
        // Short-lived: one row per issued secret, purged aggressively once consumed or expired
        // (IChallengeDialect.PurgeExpiredSql). key is capped at 450 — see the type-level remarks on
        // why not wider.
        var challenges = Create.Table(ChallengesTable)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(450).NotNullable()
            .WithColumn("purpose").AsString(100).NotNullable()
            .WithColumn("secret_hash").AsString(256).NotNullable()
            .WithColumn("secret_salt").AsString(256).NotNullable()
            .WithColumn("token_hash").AsString(256).Nullable()
            .WithColumn("attempts").AsInt32().NotNullable().WithDefaultValue(0);
        dt(challenges.WithColumn("expires_at")).NotNullable();
        dt(challenges.WithColumn("consumed_at")).Nullable();
        dt(challenges.WithColumn("created_at")).NotNullable();

        // Live-challenge lookup: IssueAsync checks for an outstanding challenge, VerifyAsync loads
        // the row to consume. Not unique — see the type-level remarks on MaxLiveChallenges.
        Create.Index("ix_challenges_scope")
            .OnTable(ChallengesTable)
            .OnColumn("tenant_id").Ascending()
            .OnColumn("key").Ascending()
            .OnColumn("purpose").Ascending();

        // VerifyByTokenAsync's lookup path (magic link / email verification) — the caller has only
        // the token, not the original key and purpose.
        Create.Index("ix_challenges_token_hash")
            .OnTable(ChallengesTable)
            .OnColumn("token_hash").Ascending();

        // Long-lived relative to challenges: counters outlive the rows they counted (see type-level
        // remarks). purpose is nullable — a null row is the per-key ceiling across every purpose,
        // the layer that protects the invoice; a non-null row is the per-scope UX limit for one
        // purpose. Purged only once a window has fully elapsed (IChallengeDialect.PurgeElapsedWindowsSql).
        var rateWindows = Create.Table(RateWindowsTable)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(450).NotNullable()
            .WithColumn("purpose").AsString(100).Nullable()
            .WithColumn("count").AsInt32().NotNullable().WithDefaultValue(0);
        dt(rateWindows.WithColumn("window_start")).NotNullable();

        // Counter lookup/upsert for both rate-limit layers: IncrementWindowSql, SelectWindowCountsSql,
        // DecrementWindowSql, and the purge all key off this same tuple.
        Create.Index("ix_challenge_rate_windows_scope")
            .OnTable(RateWindowsTable)
            .OnColumn("tenant_id").Ascending()
            .OnColumn("key").Ascending()
            .OnColumn("purpose").Ascending()
            .OnColumn("window_start").Ascending();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(RateWindowsTable);
        Delete.Table(ChallengesTable);
    }
}
