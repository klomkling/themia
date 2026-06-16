using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Framework.Data.EFCore.Repositories;

/// <summary>EF Core read/write repository. Writes are flushed by <see cref="UnitOfWork.EfUnitOfWork"/>.</summary>
public sealed class EfRepository<T, TKey>(ThemiaDbContext context, IDataFilterScope filterScope)
    : EfReadRepository<T, TKey>(context, filterScope), IRepository<T, TKey> where T : class
{
    /// <inheritdoc />
    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        // Stamp the ambient tenant onto a tenant entity added through the repository when the caller left it
        // unset. This mirrors the Dapper unit of work so both data layers honour the same insert contract.
        // (Done at the repository boundary, not in SaveChanges, so direct DbSet seeding can still write
        // explicit global/cross-tenant rows for filter testing.)
        if (entity is ITenantEntity { TenantId: null } tenantEntity
            && Context.InternalTenantContext?.CurrentTenantId is { } currentTenant)
        {
            tenantEntity.TenantId = currentTenant;
        }

        Context.Set<T>().Add(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Update(T entity) => Context.Set<T>().Update(entity);

    /// <inheritdoc />
    public void Remove(T entity) => Context.Set<T>().Remove(entity);   // ThemiaDbContext converts to soft-delete on SaveChanges

    /// <inheritdoc />
    public async Task<int> UpdateWhereAsync(
        ISpecification<T> specification,
        Action<IBulkUpdateSetters<T>> set,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(set);

        // Invoke the caller's setter lambda exactly once against a collector. The collector enforces the same
        // direct-property-access contract as the Dapper peer (uniform ArgumentException for a non-member
        // expression) and captures each setter for replay, while the empty-setter guard below enforces the same
        // InvalidOperationException as the Dapper peer (identical failure for identical misuse).
        var collector = new EfBulkUpdateSetters();
        set(collector);
        if (collector.Setters.Count == 0)
            throw new InvalidOperationException("UpdateWhereAsync requires at least one Set(...) call.");

        // Build the WHERE from the normal read path (criteria + tenant global query filter, with
        // WithoutTenantFilter honoured), then replay the collected setters onto EF's UpdateSettersBuilder.
        // The tenant predicate is thus part of the emitted UPDATE by construction.
        var affected = await CountQuery(specification).ExecuteUpdateAsync(
            builder => { foreach (var setter in collector.Setters) setter(builder); },
            cancellationToken).ConfigureAwait(false);

        // ExecuteUpdate writes straight to the database and does NOT touch the change tracker, so any
        // T already tracked in this scope still holds its pre-update column values. Detach ONLY the
        // Unchanged entries: a later read in the same scope then re-queries the DB and observes the bulk
        // write (the Dapper peer, which has no tracker, always re-queries). Modified (the caller's pending
        // edit) and Added (staged inserts) are deliberately preserved so a caller's un-saved intent is
        // never silently dropped — their later SaveChanges wins for that row (last-writer EF semantics).
        foreach (var entry in Context.ChangeTracker.Entries<T>())
            if (entry.State is EntityState.Unchanged)
                entry.State = EntityState.Detached;

        return affected;
    }

    // Collects the caller's setters in a single pass (so the caller's lambda runs exactly once). Each Set
    // validates the expression through the shared helper — a non-member-access setter fails with the same
    // uniform ArgumentException as the Dapper/fake peers — then captures a SetProperty(property, value) call
    // (the constant-value overload, so no expression trees are hand-built) for later replay onto EF Core's
    // UpdateSettersBuilder. Returns itself for chaining.
    private sealed class EfBulkUpdateSetters : IBulkUpdateSetters<T>
    {
        public List<Action<UpdateSettersBuilder<T>>> Setters { get; } = [];

        public IBulkUpdateSetters<T> Set<TProperty>(Expression<Func<T, TProperty>> property, TProperty value)
        {
            ArgumentNullException.ThrowIfNull(property);
            _ = BulkUpdateSetters.MemberName(property);   // uniform ArgumentException for a non-member expression
            Setters.Add(builder => builder.SetProperty(property, value));
            return this;
        }
    }
}
