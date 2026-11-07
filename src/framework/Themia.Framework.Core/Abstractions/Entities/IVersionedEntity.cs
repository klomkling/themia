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
/// <b>After a failed unit of work, reload the entity by id; do not re-<c>Update</c> a held instance.</b> The data
/// layer moves the instance to <c>loaded + 1</c> when it writes, before it knows the transaction will commit, so it
/// puts the version back when the write does not land: a lost race (<c>ConcurrencyException</c>), a failed save, and a
/// transaction that ends without a commit (rolled back, disposed, or a commit that threw), whether the data layer
/// opened it or the caller did with <c>BeginTransactionAsync</c>. A scope that is never disposed gets no restore until
/// the scoped context ends. On EF a rolled-back scope also clears the change tracker, as <c>ExecuteInTransactionAsync</c>
/// does, so a reload reads the row instead of the tracked instance. A save that fails inside a transaction the caller
/// still commits keeps the instances of the saves before it at their new version (their rows are committed).
/// Remaining gaps: an instance after a bulk <c>UpdateWhereAsync</c> stays behind its row (it fails closed with a
/// spurious <c>ConcurrencyException</c>), and PostgreSQL turns a <c>COMMIT</c> on a transaction aborted by a SQL error
/// into a rollback that the data layer may not see.
/// </para>
/// </remarks>
public interface IVersionedEntity
{
    /// <summary>Gets or sets the concurrency counter. Managed by the data layer; do not modify.</summary>
    long ConcurrencyVersion { get; set; }
}
