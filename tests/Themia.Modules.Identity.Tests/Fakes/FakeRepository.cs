using System.Linq.Expressions;
using System.Reflection;
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

    public Task<int> UpdateWhereAsync(
        ISpecification<T> specification,
        Action<IBulkUpdateSetters<T>> set,
        CancellationToken cancellationToken = default)
    {
        // Mirror the real set-based path: target rows by the spec's WHERE (tenant filter + criteria),
        // then write each named property directly on the matched instances.
        var setters = new FakeBulkUpdateSetters();
        set(setters);
        if (setters.Assignments.Count == 0)
            throw new InvalidOperationException("UpdateWhereAsync requires at least one Set(...) call.");

        var matched = Query(specification).ToList();
        foreach (var entity in matched)
        {
            foreach (var (property, value) in setters.Assignments)
            {
                property.SetValue(entity, value);
            }
        }
        return Task.FromResult(matched.Count);
    }

    private sealed class FakeBulkUpdateSetters : IBulkUpdateSetters<T>
    {
        public List<(PropertyInfo Property, object? Value)> Assignments { get; } = [];

        public IBulkUpdateSetters<T> Set<TProperty>(Expression<Func<T, TProperty>> property, TProperty value)
        {
            // Shared helper: a non-member-access expression fails with the same uniform ArgumentException as the
            // real peers. The resolved name maps back to the PropertyInfo for the in-memory write.
            var name = BulkUpdateSetters.MemberName(property);
            var member = typeof(T).GetProperty(name)
                ?? throw new ArgumentException($"Property '{name}' not found on {typeof(T).Name}.", nameof(property));
            Assignments.Add((member, value));
            return this;
        }
    }

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
