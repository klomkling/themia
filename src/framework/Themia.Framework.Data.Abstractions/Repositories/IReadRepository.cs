using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Framework.Data.Abstractions.Repositories;

/// <summary>Read-only repository over an aggregate/entity, queried via specifications.</summary>
public interface IReadRepository<T, in TKey> where T : class
{
    /// <summary>Fetches a single entity by key (tenant-scoped), or null.</summary>
    Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);
    /// <summary>Returns all entities matching the specification (tenant-scoped).</summary>
    /// <remarks>Result order is unspecified unless the specification defines an OrderBy term.</remarks>
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    /// <summary>Returns the first match or null.</summary>
    Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    /// <summary>Counts matches (ignoring paging).</summary>
    Task<long> CountAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    /// <summary>True if any entity matches.</summary>
    Task<bool> AnyAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    /// <summary>Returns a page of matches plus the total count.</summary>
    /// <remarks>Result order is unspecified unless the specification defines an OrderBy term.</remarks>
    Task<PagedResult<T>> PageAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
}
