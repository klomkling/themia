using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.EFCore.UnitOfWork;

/// <summary>EF Core unit of work over <see cref="ThemiaDbContext"/>.</summary>
public sealed class EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope, ISqlExceptionInterpreter sqlExceptionInterpreter) : IUnitOfWork
{
    // Versions of versioned entities as they were before the open transaction (ExecuteInTransactionAsync or a scope from
    // BeginTransactionAsync) touched them. A save inside it is accepted (the instance moves to original + 1) before the
    // transaction can still roll back, so the rollback needs the version from before the transaction to put the
    // instance back in step with the row.
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
            RestoreFailedSaveVersions();
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
            RestoreFailedSaveVersions();
            throw new UniqueConstraintException(
                "A write violated a unique or primary-key constraint: a row with the same key already exists.", ex);
        }
        catch
        {
            RestoreFailedSaveVersions();
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
            // Deleted too: a soft delete is converted to Modified and bumped inside SaveChanges, after this record.
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                versionsBeforeTransaction.TryAdd(
                    entry.Entity, (long)entry.Property(nameof(IVersionedEntity.ConcurrencyVersion)).OriginalValue!);
            }
        }
    }

    // A failed save leaves the versioned instances it was saving one ahead of their row (the save bumps the instance to
    // original + 1 before the UPDATE runs). Put the version the row still has back, so a caller that re-attaches the
    // same instance with Update carries the version it was loaded with and conflicts with a winner instead of
    // matching the winner's version and overwriting it. Only this save's entries: a transaction the caller owns keeps
    // the saves it made earlier (EF rolls a failed SaveChanges back to a savepoint), and their instances are
    // already right. The whole-transaction restore is RestoreRecordedVersions.
    private void RestoreFailedSaveVersions()
    {
        foreach (var entry in context.ChangeTracker.Entries<IVersionedEntity>().ToList())
        {
            if (entry.State is EntityState.Modified)
            {
                var version = entry.Property(nameof(IVersionedEntity.ConcurrencyVersion));
                version.CurrentValue = version.OriginalValue;
            }
        }
    }

    // The transaction ended without a commit, so every save it made is void: put each instance back to the version
    // it had before the transaction. Also reaches an instance saved in it and detached since (UpdateWhereAsync does
    // that), which the caller still holds at original + 1.
    private void RestoreRecordedVersions()
    {
        foreach (var (entity, beforeTransaction) in versionsBeforeTransaction)
        {
            if (entity is IVersionedEntity versioned)
            {
                versioned.ConcurrencyVersion = beforeTransaction;
            }
        }
    }

    private void BeginVersionRecord()
    {
        versionsBeforeTransaction.Clear();
        inTransaction = true;
    }

    private void EndVersionRecord()
    {
        inTransaction = false;
        versionsBeforeTransaction.Clear();
    }

    // The transaction ended without a commit (rolled back, disposed, or a commit that threw, after which nobody can say
    // whether it landed: an instance behind its row only costs a spurious Conflict, one ahead of it loses an update).
    // The tracker goes too: FindAsync would hand back the tracked instance with every rolled-back value, and a save of
    // it would write them again. Clear also drops Added entities, which are not in the database.
    private void AbandonTransaction()
    {
        RestoreRecordedVersions();
        context.ChangeTracker.Clear();
        EndVersionRecord();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A scope that ends without a commit (rolled back, disposed, or a commit that threw) puts every versioned instance
    /// it saved back to its pre-transaction version and clears the change tracker, as <c>ExecuteInTransactionAsync</c>
    /// does. A scope that is never disposed gets neither, until the scoped context is.
    /// </remarks>
    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var tx = await context.Database.BeginTransactionAsync(cancellationToken);
        // After Begin: a nested Begin throws, and must not touch the outer transaction's record.
        BeginVersionRecord();
        return new EfTransactionScope(tx, this);
    }

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);
            BeginVersionRecord();
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
                AbandonTransaction();
                throw;
            }
            finally
            {
                EndVersionRecord();
            }
        });
    }

    private sealed class EfTransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, EfUnitOfWork owner) : ITransactionScope
    {
        private bool ended;

        /// <inheritdoc />
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                End(abandon: true);
                throw;
            }

            End(abandon: false);
        }

        /// <inheritdoc />
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await tx.RollbackAsync(cancellationToken);
            }
            finally
            {
                End(abandon: true);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await tx.DisposeAsync();
            }
            finally
            {
                if (!ended)
                {
                    End(abandon: true);
                }
            }
        }

        private void End(bool abandon)
        {
            ended = true;
            if (abandon)
            {
                owner.AbandonTransaction();
            }
            else
            {
                owner.EndVersionRecord();
            }
        }
    }
}
