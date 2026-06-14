using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>No-op unit of work for unit tests (the fake repository mutates its list eagerly).</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        work(cancellationToken);
}
