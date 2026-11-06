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
/// </para>
/// <para>
/// Writes that bypass the repositories (raw SQL) do not bump the version. Do not set the value yourself.
/// </para>
/// </remarks>
public interface IVersionedEntity
{
    /// <summary>Gets or sets the concurrency counter. Managed by the data layer; do not modify.</summary>
    long ConcurrencyVersion { get; set; }
}
