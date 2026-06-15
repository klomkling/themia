using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Framework.Data.Abstractions.Repositories;

/// <summary>Read/write repository. Writes are flushed by the unit of work.</summary>
public interface IRepository<T, in TKey> : IReadRepository<T, TKey> where T : class
{
    /// <summary>Stages an insert.</summary>
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    /// <summary>Stages an update.</summary>
    void Update(T entity);
    /// <summary>Stages a delete (soft-delete when T implements ISoftDeletable, else hard delete).</summary>
    void Remove(T entity);

    /// <summary>
    /// Set-based update of every row matching <paramref name="specification"/>, issued as a single
    /// <c>UPDATE … SET … WHERE</c>. The specification's tenant predicate is applied so isolation holds by
    /// construction (and <see cref="Specification{T}.WithoutTenantFilter"/> is honoured exactly as for reads).
    /// Executes immediately against the database — independent of the unit of work — and participates in the
    /// ambient transaction when one is open.
    /// </summary>
    /// <remarks>
    /// Bypasses change tracking, audit stamping and soft-delete interception: it writes only the columns named
    /// in <paramref name="set"/>. Use it for direct column writes (e.g. flag/timestamp flips); callers that need
    /// audit/soft-delete semantics must load and <see cref="Update"/> entities instead.
    /// </remarks>
    /// <returns>The number of rows updated.</returns>
    Task<int> UpdateWhereAsync(
        ISpecification<T> specification,
        Action<IBulkUpdateSetters<T>> set,
        CancellationToken cancellationToken = default);
}
