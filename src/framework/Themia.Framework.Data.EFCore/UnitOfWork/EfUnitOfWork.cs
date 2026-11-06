using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.EFCore.UnitOfWork;

/// <summary>EF Core unit of work over <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope, ISqlExceptionInterpreter sqlExceptionInterpreter) : IUnitOfWork
{
    // Versions of versioned entities as they were before the running ExecuteInTransactionAsync touched them. A save
    // inside it is accepted (the instance moves to original + 1) before the transaction can still roll back, so
    // the rollback needs the version from before the transaction to put the instance back in step with the row.
    private readonly Dictionary<object, long> versionsBeforeTransaction = new(ReferenceEqualityComparer.Instance);
    private bool inTransaction;

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

        RememberVersionsBeforeTransaction();
        try
        {
            return await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The save rolled back as a whole, so every pending change of it is void, not only the entries that
            // lost the race. Left tracked, a reload in this scope would return the stale instance, a retry would
            // conflict again, and the next SaveChanges would commit the unrelated half of the failed unit of work
            // or throw the raw DbUpdateConcurrencyException. Detach everything pending; unchanged entities stay
            // readable and the next load of the losers reads the row as it now is.
            RestoreLoadedVersions();
            foreach (var entry in context.ChangeTracker.Entries().ToList())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    entry.State = EntityState.Detached;
                }
            }

            throw new ConcurrencyException(
                "A tracked update or delete affected no rows: the row does not exist, was concurrently deleted, " +
                "or is outside the current tenant scope.", ex);
        }
        catch (DbUpdateException ex) when (sqlExceptionInterpreter.IsUniqueConstraintViolation(ex))
        {
            RestoreLoadedVersions();
            throw new UniqueConstraintException(
                "A write violated a unique or primary-key constraint: a row with the same key already exists.", ex);
        }
        catch
        {
            RestoreLoadedVersions();
            throw;
        }
    }

    // Inside ExecuteInTransactionAsync, remember each versioned entity's version before its first save of the
    // transaction (the first value wins: a second save would record the already bumped one).
    private void RememberVersionsBeforeTransaction()
    {
        if (!inTransaction)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IVersionedEntity>())
        {
            if (entry.State is EntityState.Modified)
            {
                versionsBeforeTransaction.TryAdd(
                    entry.Entity, (long)entry.Property(nameof(IVersionedEntity.ConcurrencyVersion)).OriginalValue!);
            }
        }
    }

    // A failed or rolled-back write leaves versioned instances one ahead of their row (the save bumps the instance to
    // original + 1 before the UPDATE runs). Put the version the row still has back, so a caller that re-attaches the
    // same instance with Update carries the version it was loaded with and conflicts with a winner instead of
    // matching the winner's version and overwriting it. An entity saved earlier in the same rolled-back transaction
    // was already accepted, so its pre-transaction version comes from the record, not from EF's original value.
    private void RestoreLoadedVersions()
    {
        foreach (var entry in context.ChangeTracker.Entries<IVersionedEntity>().ToList())
        {
            var version = entry.Property(nameof(IVersionedEntity.ConcurrencyVersion));
            if (versionsBeforeTransaction.TryGetValue(entry.Entity, out var beforeTransaction))
            {
                version.CurrentValue = beforeTransaction;
            }
            else if (entry.State is EntityState.Modified)
            {
                version.CurrentValue = version.OriginalValue;
            }
        }

        // An instance saved earlier in the transaction and detached since (UpdateWhereAsync does that) is no longer
        // tracked, but the caller still holds it at original + 1.
        foreach (var (entity, beforeTransaction) in versionsBeforeTransaction)
        {
            if (entity is IVersionedEntity versioned && context.Entry(entity).State is EntityState.Detached)
            {
                versioned.ConcurrencyVersion = beforeTransaction;
            }
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
            versionsBeforeTransaction.Clear();
            inTransaction = true;
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
                RestoreLoadedVersions();
                context.ChangeTracker.Clear();
                throw;
            }
            finally
            {
                inTransaction = false;
                versionsBeforeTransaction.Clear();
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
