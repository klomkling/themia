namespace Themia.Framework.Core.Abstractions.Entities;

/// <summary>
/// Opts an entity into portable, application-incremented optimistic concurrency: a <see cref="long"/>
/// counter that every full-row write checks and bumps, identically on EF Core and Dapper and on every
/// supported engine.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="IConcurrencyAware"/> (an engine-specific <c>byte[]</c> token: <c>rowversion</c> on SQL
/// Server, <c>xmin</c> on PostgreSQL, unsupported on MySQL, and ignored by Dapper), this counter behaves the
/// same everywhere. The column is <c>concurrency_version</c>.
/// </para>
/// <para>
/// <b>Contract.</b> A tracked <c>Update</c> succeeds only if the stored version equals the version the entity
/// was loaded with, and writes <c>loaded + 1</c> in the same UPDATE; otherwise it fails with
/// <c>ConcurrencyException</c>. Set-based <c>UpdateWhereAsync</c> writes also bump the version (they keep
/// their own predicates and do not check it), so a stale full-row <c>Update</c> loaded before a bulk write
/// conflicts instead of overwriting it. Consequently a bulk write followed by an <c>Update</c> of an entity
/// loaded earlier in the same scope throws <c>ConcurrencyException</c>.
/// A bulk write that is only bookkeeping (a login clearing its own failure counter) opts out with
/// <c>WithoutVersionBump()</c> on the setters, so a stream of them cannot keep other writers losing their check.
/// </para>
/// <para>
/// Writes that bypass the repositories (raw SQL) do not bump the version. Do not set the value yourself.
/// </para>
/// <para>
/// <b>After a failed or rolled-back unit of work, reload the entity; never re-<c>Update</c> a held
/// instance.</b> The data layer moves the instance to <c>loaded + 1</c> when it writes, before it knows the
/// transaction will commit. When the write is later rolled back, the row is still at <c>loaded</c> but the
/// instance may keep <c>loaded + 1</c>, which is exactly the version a concurrent winner writes: saving that
/// same instance again could then overwrite the winner. A lost race (<c>ConcurrencyException</c>), the common
/// failures and a rolled-back <c>ExecuteInTransactionAsync</c> are put back in step, but these known paths are
/// not: a Dapper unit of work with several operations where an earlier one succeeded and a later one failed,
/// and an EF transaction opened with <c>BeginTransactionAsync</c> and rolled back by hand. Without a winner the stale
/// instance fails closed (a spurious <c>ConcurrencyException</c>), so the exposure is a caller that holds
/// an instance across the failure and a writer that lands in between. Reload by id instead.
/// </para>
/// </remarks>
public interface IVersionedEntity
{
    /// <summary>Gets or sets the concurrency counter. Managed by the data layer; do not modify.</summary>
    long ConcurrencyVersion { get; set; }
}
