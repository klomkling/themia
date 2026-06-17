using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.EFCore.UnitOfWork;

/// <summary>EF Core unit of work over <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope, ISqlExceptionInterpreter sqlExceptionInterpreter) : IUnitOfWork
{
    /// <summary>
    /// Back-compatible overload matching the original signature. The tenant-filter bypass state is
    /// process-ambient (a static <see cref="DataFilterScope"/> async-local), so a fresh instance observes the
    /// same bypass as the DI-registered one. DI selects the greediest resolvable constructor.
    /// </summary>
    public EfUnitOfWork(ThemiaDbContext context) : this(context, new DataFilterScope()) { }

    /// <summary>
    /// Back-compatible overload that defaults to the SQLSTATE-based unique-violation interpreter
    /// (PostgreSQL/MySQL). DI prefers the three-parameter constructor and injects the engine's registered
    /// <see cref="ISqlExceptionInterpreter"/> (e.g. the SQL Server number-based one).
    /// </summary>
    public EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope)
        : this(context, filterScope, new SqlStateUniqueConstraintInterpreter()) { }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);

    // Translate EF's write failures into the framework's provider-agnostic exceptions so both data layers
    // surface them the same way: optimistic-concurrency (a tracked update/delete that affected no rows) ->
    // ConcurrencyException; a unique/primary-key violation -> UniqueConstraintException.
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
        catch (DbUpdateException ex) when (sqlExceptionInterpreter.IsUniqueConstraintViolation(ex))
        {
            throw new UniqueConstraintException(
                "A write violated a unique or primary-key constraint: a row with the same key already exists.", ex);
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
            try
            {
                await work(cancellationToken);
                await SaveAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                // The transaction rolled back, so the entities staged by `work` no longer exist in the
                // database — but EF still tracks them (Added/Modified). Discard that tracker state so a
                // caller that retries on the same scoped DbContext (e.g. a concurrency-race retry loop)
                // does not re-attempt the failed writes against a clean transaction.
                context.ChangeTracker.Clear();
                throw;
            }
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
