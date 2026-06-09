using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.EFCore.UnitOfWork;

/// <summary>EF Core unit of work over <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope) : IUnitOfWork
{
    /// <summary>
    /// Back-compatible overload matching the original signature. The tenant-filter bypass state is
    /// process-ambient (a static <see cref="DataFilterScope"/> async-local), so a fresh instance observes the
    /// same bypass as the DI-registered one. DI selects the two-parameter constructor (greediest resolvable).
    /// </summary>
    public EfUnitOfWork(ThemiaDbContext context) : this(context, new DataFilterScope()) { }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);

    // Translate EF's optimistic-concurrency failure (a tracked update/delete that affected no rows) into the
    // framework's provider-agnostic ConcurrencyException so both data layers surface a lost write the same way.
    private async Task<int> SaveAsync(CancellationToken cancellationToken)
    {
        if (!filterScope.IsTenantFilterBypassed)
        {
            await context.ValidateTenantWritesAsync(cancellationToken);
        }

        try
        {
            return await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException(
                "A tracked update or delete affected no rows: the row does not exist, was concurrently deleted, " +
                "or is outside the current tenant scope.", ex);
        }
    }

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
            await SaveAsync(cancellationToken);
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
