using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>In-memory IRepository for service unit tests. Honors soft-delete, tenant filtering, and IgnoreTenantFilter.</summary>
internal sealed class FakeRepository<T>(List<T> store, Func<T, Guid> idSelector) : IRepository<T, Guid>
    where T : class
{
    public TenantId? AmbientTenant { get; set; }

    private bool TenantMatches(T e) =>
        e is not ITenantEntity te || Nullable.Equals(te.TenantId, AmbientTenant);

    private static bool NotDeleted(T e) => e is not ISoftDeletable sd || !sd.IsDeleted;

    private IEnumerable<T> Query(ISpecification<T> spec)
    {
        IEnumerable<T> q = store.Where(NotDeleted);
        if (!spec.IgnoreTenantFilter)
        {
            q = q.Where(TenantMatches);
        }
        if (spec.Criteria is not null)
        {
            q = q.Where(spec.Criteria.Compile());
        }
        return q;
    }

    public Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.FirstOrDefault(e => NotDeleted(e) && TenantMatches(e) && idSelector(e) == id));

    public Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult(Query(specification).FirstOrDefault());

    public Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<T>>(Query(specification).ToList());

    public Task<long> CountAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult<long>(Query(specification).LongCount());

    public Task<bool> AnyAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult(Query(specification).Any());

    public Task<PagedResult<T>> PageAsync(ISpecification<T> specification, CancellationToken cancellationToken = default)
    {
        var all = Query(specification).ToList();
        return Task.FromResult(new PagedResult<T>(all, all.Count, 0, all.Count));
    }

    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        if (entity is ITenantEntity { TenantId: null } te && AmbientTenant is { } t)
        {
            te.TenantId = t;   // mirror the real repos' ambient-tenant stamp on add
        }
        store.Add(entity);
        return Task.CompletedTask;
    }

    public void Update(T entity) { /* in-memory: the instance is already mutated */ }

    public void Remove(T entity)
    {
        if (entity is ISoftDeletable)
        {
            // Mirror ThemiaDbContext: Remove on a soft-deletable converts to soft-delete.
            var prop = typeof(T).GetProperty(nameof(ISoftDeletable.IsDeleted))!;
            prop.SetValue(entity, true);
        }
        else
        {
            store.Remove(entity);
        }
    }
}
