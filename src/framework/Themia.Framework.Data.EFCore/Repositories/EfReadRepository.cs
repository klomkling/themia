using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Framework.Data.EFCore.Repositories;

/// <summary>EF Core read repository over <see cref="ThemiaDbContext"/>, driven by specifications.</summary>
public class EfReadRepository<T, TKey>(ThemiaDbContext context, IDataFilterScope filterScope) : IReadRepository<T, TKey>
    where T : class
{
    /// <summary>The underlying EF context.</summary>
    protected ThemiaDbContext Context { get; } = context;

    /// <summary>Builds the queryable for a specification (criteria + ordering + paging + optional tenant bypass).</summary>
    protected IQueryable<T> Query(ISpecification<T> spec)
    {
        IQueryable<T> q = Context.Set<T>();
        if (spec.IgnoreTenantFilter || filterScope.IsTenantFilterBypassed) q = q.IgnoreQueryFilters();
        if (spec.Criteria is not null) q = q.Where(spec.Criteria);

        IOrderedQueryable<T>? ordered = null;
        foreach (var o in spec.OrderBy)
            ordered = ordered is null
                ? (o.Descending ? q.OrderByDescending(o.KeySelector) : q.OrderBy(o.KeySelector))
                : (o.Descending ? ordered.ThenByDescending(o.KeySelector) : ordered.ThenBy(o.KeySelector));
        if (ordered is not null) q = ordered;

        if (spec.Skip is { } s) q = q.Skip(s);
        if (spec.Take is { } t) q = q.Take(t);
        return q;
    }

    /// <inheritdoc />
    public async Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        // Route through the context's guarded FindAsync (not DbSet.FindAsync): for an entity already
        // tracked in this scope, DbSet.FindAsync returns the tracked instance without re-applying the
        // tenant/soft-delete query filter. ThemiaDbContext.FindAsync<T> re-checks tenant + IsDeleted via
        // ValidateTenantAccess, so a soft-deleted or cross-tenant row tracked in-scope is hidden here —
        // matching the Dapper layer, which always re-queries with the filter.
        => await Context.FindAsync<T>([id!], cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await Query(spec).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await Query(spec).FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<long> CountAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountQuery(spec).LongCountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> AnyAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountQuery(spec).AnyAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<PagedResult<T>> PageAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(spec, cancellationToken);
        var items = await ListAsync(spec, cancellationToken);
        return new PagedResult<T>(items, total, spec.Skip, spec.Take);
    }

    private IQueryable<T> CountQuery(ISpecification<T> spec)
    {
        IQueryable<T> q = Context.Set<T>();
        if (spec.IgnoreTenantFilter || filterScope.IsTenantFilterBypassed) q = q.IgnoreQueryFilters();
        if (spec.Criteria is not null) q = q.Where(spec.Criteria);
        return q;
    }
}
