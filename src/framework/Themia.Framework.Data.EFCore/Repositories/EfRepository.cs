using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;

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
}
