using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Challenges.Migrations;

/// <summary>Creates the <c>challenges</c> and <c>challenge_rate_windows</c> tables on PostgreSQL,
/// MySQL, and SQL Server. FluentMigrator is the single DDL authority for both the EF and
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
/// <c>key</c> is also a reserved word on MySQL and SQL Server (not on PostgreSQL), so every raw SQL
/// statement below that touches it — and every statement <see cref="IChallengeDialect"/>'s engine
/// packages write against this schema — quotes it per engine (<c>"key"</c> / <c>`key`</c> /
/// <c>[key]</c>); an unquoted <c>key</c> in raw SQL is a parse error on two of the three engines.
/// </para>
/// <para>
/// <b><c>challenges</c> indexes are deliberately plain, not unique.</b>
/// <c>PurposeOptions.MaxLiveChallenges</c> can be configured above its default of 1, which
/// legitimately allows more than one live row for the same <c>(tenant_id, key, purpose)</c> at once.
/// A unique constraint — even one filtered to live rows — would reject that configuration outright,
/// so uniqueness there is enforced by the engine's re-issue policy
/// (<see cref="IChallengeDialect.InvalidateLiveForScopeSql"/>), not by the schema. This reasoning is
/// specific to <c>challenges</c> — it does not carry over to <c>challenge_rate_windows</c>, whose
/// indexes are unique for the opposite reason; see <see cref="CreateRateWindowUniqueIndexes"/>.
/// </para>
/// <para>
/// <b><c>challenge_rate_windows</c> has no <c>created_at</c>/<c>updated_at</c></b>, unlike the
/// repo's usual convention: <c>window_start</c> already is the row's timestamp — it identifies which
/// fixed-width bucket the row counts — so a separate insertion-time column would just duplicate it
/// with a different, purge-irrelevant value.
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

        // Filtered/functional unique indexes are not expressible via the fluent API, so they are
        // emitted as raw SQL with `key` quoted per engine (see the type-level remarks).
        IfDatabase("postgresql").Delegate(() => CreateRateWindowUniqueIndexes("\"key\""));
        IfDatabase("sqlserver").Delegate(() => CreateRateWindowUniqueIndexes("[key]"));
        IfDatabase("mysql").Delegate(CreateMySqlRateWindowUniqueIndexes);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Challenges supports only PostgreSQL, MySQL, and SQL Server. The active " +
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
        // purpose. Uniqueness is created separately below, per engine — see
        // CreateRateWindowUniqueIndexes / CreateMySqlRateWindowUniqueIndexes.
        var rateWindows = Create.Table(RateWindowsTable)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(450).NotNullable()
            .WithColumn("purpose").AsString(100).Nullable()
            .WithColumn("count").AsInt32().NotNullable().WithDefaultValue(0);
        dt(rateWindows.WithColumn("window_start")).NotNullable();
    }

    /// <summary>
    /// Emits the four filtered unique indexes on PostgreSQL and SQL Server that together make one
    /// <c>(tenant_id, key, purpose, window_start)</c> bucket resolve to exactly one row — required
    /// for <see cref="IChallengeDialect.IncrementWindowSql"/>'s atomic upsert, which needs a real
    /// unique constraint to target: PostgreSQL's <c>ON CONFLICT</c> errors (<c>42P10</c>) without
    /// one, and without one on any engine, concurrent first-increments for the same bucket race
    /// past each other and insert two half-counted rows for what must be a single counter — silently
    /// double-booking the <c>purpose IS NULL</c> row, which is the per-key ceiling this schema exists
    /// to protect.
    /// <para>
    /// A single <c>UNIQUE(tenant_id, key, purpose, window_start)</c> does not work: both
    /// <c>tenant_id</c> and <c>purpose</c> are nullable, and PostgreSQL/SQL Server (like every SQL
    /// engine) treat two NULLs as unequal in an ordinary index, so a plain unique index would let
    /// unlimited duplicate rows accumulate wherever either column is NULL — exactly the platform-level
    /// and per-key-ceiling rows this table depends on being unique. Four partial indexes, one per
    /// NULL-combination of <c>tenant_id</c>/<c>purpose</c>, restore exact-one-row semantics per
    /// combination without adding columns to the table. This mirrors
    /// <c>Themia.Modules.Pdf.Migrations.PdfTemplateSchemaMigration</c>'s per-tenant/global filtered
    /// unique indexes on <c>pdf_templates</c> — same technique, extended from one nullable column to
    /// two. A COALESCE-to-sentinel expression index (viable on PostgreSQL and MySQL) was rejected
    /// because SQL Server's <c>CREATE INDEX</c> has no expression-index syntax — only computed-column
    /// indexes, which would mean adding a column not in this schema.
    /// </para>
    /// </summary>
    /// <param name="keyColumn">The quoted <c>key</c> identifier for the target engine (<c>"key"</c> on
    /// PostgreSQL, <c>[key]</c> on SQL Server — <c>key</c> is a reserved word on SQL Server).</param>
    private void CreateRateWindowUniqueIndexes(string keyColumn)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_tenant_purpose ON {RateWindowsTable} (tenant_id, {keyColumn}, purpose, window_start) WHERE tenant_id IS NOT NULL AND purpose IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_tenant_keyonly ON {RateWindowsTable} (tenant_id, {keyColumn}, window_start) WHERE tenant_id IS NOT NULL AND purpose IS NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_platform_purpose ON {RateWindowsTable} ({keyColumn}, purpose, window_start) WHERE tenant_id IS NULL AND purpose IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_platform_keyonly ON {RateWindowsTable} ({keyColumn}, window_start) WHERE tenant_id IS NULL AND purpose IS NULL;");
    }

    /// <summary>
    /// MySQL has no partial/filtered indexes, so the same four NULL-combination scopes from
    /// <see cref="CreateRateWindowUniqueIndexes"/> are emulated with functional key parts that fold to
    /// NULL for every row outside that scope. MySQL treats each NULL as distinct in a unique index, so
    /// folded-out rows never collide with each other or with a real match — the same technique
    /// <c>Themia.Modules.Pdf.Migrations.PdfTemplateSchemaMigration</c> uses for <c>pdf_templates</c> on
    /// MySQL, extended to two nullable columns. Deliberately used instead of MySQL's
    /// <c>INSERT ... ON DUPLICATE KEY UPDATE</c> relying on a plain index: that construct fires only on
    /// a real unique-key violation, so against a plain index it would silently insert a second row
    /// per bucket instead of updating the first.
    /// <para>
    /// <b>Requires MySQL 8.0.13+, and does not run on MariaDB at any version</b> — functional key parts
    /// are the syntax being used here, and MariaDB has no equivalent (its generated-column route needs a
    /// different schema, not a different index). This is the concrete reason MariaDB is not a supported
    /// engine; see "Multi-database requirement" in <c>docs/themia-architecture-overview.md</c>.
    /// </para>
    /// </summary>
    private void CreateMySqlRateWindowUniqueIndexes()
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_tenant_purpose ON {RateWindowsTable} " +
            "((IF(tenant_id IS NULL OR purpose IS NULL, NULL, tenant_id)), (IF(tenant_id IS NULL OR purpose IS NULL, NULL, `key`)), " +
            "(IF(tenant_id IS NULL OR purpose IS NULL, NULL, purpose)), (IF(tenant_id IS NULL OR purpose IS NULL, NULL, window_start)));");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_tenant_keyonly ON {RateWindowsTable} " +
            "((IF(tenant_id IS NULL OR purpose IS NOT NULL, NULL, tenant_id)), (IF(tenant_id IS NULL OR purpose IS NOT NULL, NULL, `key`)), " +
            "(IF(tenant_id IS NULL OR purpose IS NOT NULL, NULL, window_start)));");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_platform_purpose ON {RateWindowsTable} " +
            "((IF(tenant_id IS NOT NULL OR purpose IS NULL, NULL, `key`)), (IF(tenant_id IS NOT NULL OR purpose IS NULL, NULL, purpose)), " +
            "(IF(tenant_id IS NOT NULL OR purpose IS NULL, NULL, window_start)));");
        Execute.Sql($"CREATE UNIQUE INDEX ux_challenge_rate_windows_platform_keyonly ON {RateWindowsTable} " +
            "((IF(tenant_id IS NOT NULL OR purpose IS NOT NULL, NULL, `key`)), (IF(tenant_id IS NOT NULL OR purpose IS NOT NULL, NULL, window_start)));");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(RateWindowsTable);
        Delete.Table(ChallengesTable);
    }
}
