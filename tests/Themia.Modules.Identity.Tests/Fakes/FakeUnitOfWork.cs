using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>No-op unit of work for unit tests (the fake repository mutates its list eagerly).</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    /// <summary>Makes every save throw, to prove what an earlier step leaves behind when the final write fails.</summary>
    public bool ThrowOnSave { get; set; }

    /// <summary>How many saves (or transactions) still fail with a ConcurrencyException before one succeeds.</summary>
    public int ConcurrencyFailures { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnSave)
        {
            throw new InvalidOperationException("save failed");
        }

        ThrowIfLosingRace();
        SaveCount++;
        return Task.FromResult(0);
    }

    public Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ThrowIfLosingRace();
        return work(cancellationToken);
    }

    private void ThrowIfLosingRace()
    {
        if (ConcurrencyFailures > 0)
        {
            ConcurrencyFailures--;
            throw new ConcurrencyException("The row changed after it was loaded.");
        }
    }
}
