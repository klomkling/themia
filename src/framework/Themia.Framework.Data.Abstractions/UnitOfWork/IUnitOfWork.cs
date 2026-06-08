namespace Themia.Framework.Data.Abstractions.UnitOfWork;

/// <summary>Flushes staged writes and owns transaction boundaries.</summary>
public interface IUnitOfWork
{
    /// <summary>Flushes pending writes; returns the number of affected rows.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Begins an explicit transaction.</summary>
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs <paramref name="work"/> inside a transaction, saving and committing on success.</summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}
