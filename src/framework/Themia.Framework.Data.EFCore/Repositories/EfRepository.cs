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
        Context.Set<T>().Add(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Update(T entity) => Context.Set<T>().Update(entity);

    /// <inheritdoc />
    public void Remove(T entity) => Context.Set<T>().Remove(entity);   // ThemiaDbContext converts to soft-delete on SaveChanges
}
