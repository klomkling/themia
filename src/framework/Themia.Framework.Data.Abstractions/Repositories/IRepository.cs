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
}
