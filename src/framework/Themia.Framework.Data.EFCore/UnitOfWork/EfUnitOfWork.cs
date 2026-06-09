using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.EFCore.UnitOfWork;

/// <summary>EF Core unit of work over <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfUnitOfWork(ThemiaDbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => new EfTransactionScope(await context.Database.BeginTransactionAsync(cancellationToken));

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);
            await work(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private sealed class EfTransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : ITransactionScope
    {
        /// <inheritdoc />
        public Task CommitAsync(CancellationToken cancellationToken = default) => tx.CommitAsync(cancellationToken);

        /// <inheritdoc />
        public Task RollbackAsync(CancellationToken cancellationToken = default) => tx.RollbackAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }
}
