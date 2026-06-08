namespace Themia.Framework.Data.Abstractions.UnitOfWork;

/// <summary>An explicit transaction boundary. Dispose rolls back if not committed.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>Commits the transaction.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
    /// <summary>Rolls back the transaction.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
