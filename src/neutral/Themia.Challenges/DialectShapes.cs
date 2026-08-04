namespace Themia.Challenges;

/// <summary>
/// Which nullability combination of a <c>challenge_rate_windows</c> bucket a statement targets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the SQL is selected rather than parameterised.</b> Both <c>tenant_id</c> and <c>purpose</c> are
/// nullable on that table, so a single statement covering every case has to compare them with a
/// null-safe operator (<c>IS NOT DISTINCT FROM</c>, <c>&lt;=&gt;</c>, or SQL Server's OR-guard). Every
/// one of those forms is <b>non-sargable</b>: no index can be seeked through it. Measured on
/// PostgreSQL 16 against 200 000 rows, the null-safe <c>UPDATE</c> — the statement
/// <c>IssueAsync</c> runs two or three times per call — planned as a sequential scan removing all
/// 200 000 rows, 1921 buffers, 16.2 ms. The same row through a shape-specific predicate planned as an
/// index scan: 3 buffers, 0.042 ms. All four shapes behaved the same way, and the OR-guard form does
/// not recover it even with literal values, so this is a change of SQL text rather than of hint.
/// </para>
/// <para>
/// The four members map one-to-one onto the four filtered/functional unique indexes
/// <c>ChallengeSchemaMigration</c> creates, which is what makes each shape's predicate seekable — and is
/// also the check that a new shape has a home. Selecting SQL by shape keeps the null-safety guarantee
/// (each variant states its own <c>IS NULL</c> / <c>= @Param</c> explicitly, so nothing falls back to a
/// plain <c>=</c> against a <see langword="null"/> parameter) while letting the planner see a constant.
/// </para>
/// </remarks>
public enum RateWindowBucket
{
    /// <summary>Tenant-scoped, one purpose. <c>tenant_id</c> and <c>purpose</c> both bound.</summary>
    TenantAndPurpose,

    /// <summary>Tenant-scoped, every purpose — the per-key ceiling. <c>tenant_id</c> bound, <c>purpose IS NULL</c>.</summary>
    TenantAllPurposes,

    /// <summary>Platform-level, one purpose. <c>tenant_id IS NULL</c>, <c>purpose</c> bound.</summary>
    PlatformAndPurpose,

    /// <summary>Platform-level, every purpose. <c>tenant_id IS NULL</c> and <c>purpose IS NULL</c>.</summary>
    PlatformAllPurposes,
}

/// <summary>
/// Whether a <c>challenges</c> statement targets a tenant's rows or the platform-level ones.
/// </summary>
/// <remarks>
/// Same reason as <see cref="RateWindowBucket"/>: <c>tenant_id</c> is nullable on <c>challenges</c>, so
/// one statement covering both cases needs a non-sargable null-safe comparison and loses
/// <c>ix_challenges_scope</c>, whose leading column it is. <c>purpose</c> is <c>NOT NULL</c> there, so
/// two shapes suffice rather than four.
/// </remarks>
public enum ChallengeTenancy
{
    /// <summary><c>tenant_id = @TenantId</c>.</summary>
    Tenant,

    /// <summary><c>tenant_id IS NULL</c> — a challenge that belongs to no tenant.</summary>
    Platform,
}
