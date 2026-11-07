using global::Dapper;
using SqlKata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.UnitOfWork;

internal sealed class DapperUnitOfWork(
    IDapperConnectionContext connection,
    EntityMappingRegistry registry,
    ISqlCompiler compiler,
    ITenantContext tenantContext,
    ICurrentUserAccessor currentUser,
    IDataFilterScope filterScope,
    TimeProvider timeProvider,
    ISqlExceptionInterpreter sqlExceptionInterpreter) : IUnitOfWork, IPendingOperationSink
{
    private readonly List<PendingOperation> pending = [];

    // Versions of versioned entities as they were before the open transaction (the one SaveChangesAsync opens, or a
    // scope from BeginTransactionAsync) first saved them. A save moves the instance to loaded + 1 before the
    // transaction can still roll back, so a rollback needs these to put each instance back in step with its row.
    private readonly Dictionary<IVersionedEntity, long> versionsBeforeTransaction = new(ReferenceEqualityComparer.Instance);
    private bool recordingVersions;

    public void Enqueue(PendingOperation operation) => pending.Add(operation);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (pending.Count == 0) return 0;
        var ownsTransaction = connection.CurrentTransaction is null;
        if (ownsTransaction)
        {
            await connection.BeginTransactionAsync(cancellationToken);
            BeginVersionRecord();
        }

        var conn = await connection.GetOpenConnectionAsync(cancellationToken);
        var tx = connection.CurrentTransaction;
        try
        {
            var affected = 0;
            foreach (var op in pending)
                affected += await ExecuteAsync(conn, tx, op, cancellationToken);
            if (ownsTransaction && tx is not null)
                await tx.CommitAsync(cancellationToken);
            if (ownsTransaction) EndVersionRecord();
            pending.Clear();
            return affected;
        }
        catch (Exception ex)
        {
            pending.Clear();
            // Only a transaction this save opened is void as a whole. In the caller's, an earlier op of this batch
            // stays in it at loaded + 1 (there is no savepoint), and the caller's own rollback restores it.
            if (ownsTransaction) RestoreRecordedVersions();
            if (ownsTransaction && tx is not null)
            {
                // Roll back with CancellationToken.None so a cancelled token can't abort the cleanup,
                // and swallow a rollback failure so it doesn't mask the original error (rethrown below).
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { /* preserve the original exception */ }
            }
            // Translate a unique/primary-key violation into the framework's provider-agnostic exception
            // (still after rollback) so both data layers surface a duplicate-key write the same way.
            if (sqlExceptionInterpreter.IsUniqueConstraintViolation(ex))
            {
                throw new UniqueConstraintException(
                    "A write violated a unique or primary-key constraint: a row with the same key already exists.", ex);
            }
            throw;
        }
        finally
        {
            if (ownsTransaction) await connection.DisposeTransactionAsync();
        }
    }

    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        await connection.BeginTransactionAsync(cancellationToken);
        // After Begin: a nested Begin throws, and must not touch the outer transaction's record.
        BeginVersionRecord();
        return new TransactionScope(connection, this);
    }

    internal void BeginVersionRecord()
    {
        versionsBeforeTransaction.Clear();
        recordingVersions = true;
    }

    internal void EndVersionRecord()
    {
        recordingVersions = false;
        versionsBeforeTransaction.Clear();
    }

    // First value wins: a second save of the same instance would record the version the first one already bumped.
    internal void RememberVersion(IVersionedEntity entity, long loadedVersion)
    {
        if (recordingVersions) versionsBeforeTransaction.TryAdd(entity, loadedVersion);
    }

    // The transaction ended without a commit (rolled back, disposed, or a commit that threw, after which nobody can
    // say whether it landed: an instance behind its row only costs a spurious Conflict, one ahead of it loses an
    // update), so every save it made is void.
    internal void RestoreRecordedVersions()
    {
        foreach (var (entity, loadedVersion) in versionsBeforeTransaction)
            entity.ConcurrencyVersion = loadedVersion;
        EndVersionRecord();
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        await using var scope = await BeginTransactionAsync(cancellationToken);
        try
        {
            await work(cancellationToken);
            await SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }
        catch
        {
            // Roll back with CancellationToken.None so a cancelled token can't abort the cleanup, and
            // swallow a rollback failure so it doesn't mask the original exception (rethrown below).
            try { await scope.RollbackAsync(CancellationToken.None); }
            catch { /* preserve the original exception */ }
            throw;
        }
    }

    private async Task<int> ExecuteAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, PendingOperation op, CancellationToken ct)
    {
        var map = registry.For(op.EntityType);
        var now = timeProvider.GetUtcNow();
        var userId = currentUser.UserId;
        switch (op.Kind)
        {
            case PendingKind.Add:
            {
                if (op.Entity is ITenantEntity te && te.TenantId is null)
                    te.TenantId = tenantContext.CurrentTenantId;
                map.SetValue(op.Entity, nameof(IAuditableEntity.CreatedAt), now);
                if (userId is not null) map.SetValue(op.Entity, nameof(IAuditableEntity.CreatedBy), userId);
                var values = ColumnValues(op.Entity, map, out var keyAssigned);
                if (!keyAssigned && !string.Equals(map.KeyColumn, "id", StringComparison.Ordinal))
                    throw new NotSupportedException(
                        $"Store-generated keys require the key column on '{map.Table}' to be named 'id' (found '{map.KeyColumn}'). " +
                        "Assign the key client-side before AddAsync, or keep the conventional 'id' key column.");
                var query = new Query(map.Table).AsInsert(values, returnId: !keyAssigned);
                var sql = compiler.Compile(query);
                if (!keyAssigned)
                {
                    var newId = await conn.ExecuteScalarAsync<object>(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
                    if (newId is null or System.DBNull)
                        throw new InvalidOperationException($"INSERT into '{map.Table}' requested a store-generated key (the '{map.KeyProperty}' was unassigned) but the database returned no id. Assign the key before AddAsync, or ensure the key column is auto-generated.");
                    map.KeySetter(op.Entity, ConvertKey(newId, map.KeyType));
                    return 1;
                }
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
            }
            case PendingKind.Update:
            {
                map.SetValue(op.Entity, nameof(IAuditableEntity.LastModifiedAt), now);
                if (userId is not null) map.SetValue(op.Entity, nameof(IAuditableEntity.LastModifiedBy), userId);
                var values = ColumnValues(op.Entity, map, out _);
                values.Remove(map.KeyColumn);   // never update the key
                // Creation + tenant columns are immutable after insert; don't let a partial entity clobber
                // them (EF only writes changed columns, so this keeps the two providers consistent).
                RemoveColumn(values, map, nameof(IAuditableEntity.CreatedAt));
                RemoveColumn(values, map, nameof(IAuditableEntity.CreatedBy));
                RemoveColumn(values, map, nameof(ITenantEntity.TenantId));
                var query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map));
                var loadedVersion = VersionColumn(op.Entity, map, out var versionColumn)
                    ? (long)map.GetValue(op.Entity, nameof(IVersionedEntity.ConcurrencyVersion))!
                    : 0L;
                if (versionColumn is not null)
                {
                    RememberVersion((IVersionedEntity)op.Entity, loadedVersion);
                    // Optimistic concurrency: only the row still at the loaded version may be overwritten, and the
                    // same UPDATE moves it to loaded + 1. A stale full-row write matches no row -> ConcurrencyException.
                    values[versionColumn] = loadedVersion + 1;
                    query = query.Where(versionColumn, loadedVersion);
                }
                var sql = compiler.Compile(query.AsUpdate(values));
                var affected = await ExecuteSingleAsync(conn, tx, sql, op, map, "Update", ct);
                // Keep the instance in step so a second Update of it in the same scope does not conflict with itself.
                if (versionColumn is not null)
                    map.SetValue(op.Entity, nameof(IVersionedEntity.ConcurrencyVersion), loadedVersion + 1);
                return affected;
            }
            case PendingKind.Remove:
            {
                Query query;
                if (op.Entity is ISoftDeletable)
                {
                    var values = new Dictionary<string, object?>
                    {
                        [map.Column(nameof(ISoftDeletable.IsDeleted))] = true,
                        [map.Column(nameof(ISoftDeletable.DeletedAt))] = now,
                        [map.Column(nameof(ISoftDeletable.DeletedBy))] = userId,
                    };
                    if (VersionColumn(op.Entity, map, out var removeVersionColumn))
                        values[removeVersionColumn!] = VersionedSql.Increment(removeVersionColumn!);
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsUpdate(values);
                }
                else
                {
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsDelete();
                }
                var sql = compiler.Compile(query);
                return await ExecuteSingleAsync(conn, tx, sql, op, map, "Remove", ct);
            }
            default: return 0;
        }
    }

    // A single-entity Update/Remove that matches no row is a lost write (missing row, concurrent delete, or
    // outside the tenant scope). Fail loud with the same contract as EF's optimistic-concurrency failure.
    private static async Task<int> ExecuteSingleAsync(
        System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, CompiledSql sql,
        PendingOperation op, EntityMapping map, string operation, CancellationToken ct)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
        if (affected == 0)
            throw new ConcurrencyException(
                $"{operation} of '{map.Table}' (key '{KeyOf(op.Entity, map)}') affected no rows: the row does not exist, " +
                "was concurrently deleted, or is outside the current tenant scope.");
        return affected;
    }

    // True when the entity opts into IVersionedEntity and its mapping carries the counter column.
    private static bool VersionColumn(object entity, EntityMapping map, out string? column)
    {
        column = entity is IVersionedEntity && map.Columns.TryGetValue(nameof(IVersionedEntity.ConcurrencyVersion), out var c) ? c : null;
        return column is not null;
    }

    private static void RemoveColumn(Dictionary<string, object?> values, EntityMapping map, string property)
    {
        if (map.Columns.TryGetValue(property, out var column))
            values.Remove(column);
    }

    // Scope the write to the ambient tenant's rows; with no ambient tenant, restrict to global
    // (tenant_id IS NULL) rows so a system context cannot mutate a tenant-owned row by primary key.
    // Under an active bypass scope the predicate is dropped (admin/migration cross-tenant write).
    private Query TenantScoped(Query q, object entity, EntityMapping map)
    {
        if (entity is ITenantEntity && !filterScope.IsTenantFilterBypassed)
        {
            var column = map.Column(nameof(ITenantEntity.TenantId));
            if (tenantContext.CurrentTenantId is { } t)
                q.Where(column, t.Value);
            else
                q.WhereNull(column);
        }
        return q;
    }

    private static object KeyOf(object entity, EntityMapping map) => map.GetValue(entity, map.KeyProperty)!;

    private static Dictionary<string, object?> ColumnValues(object entity, EntityMapping map, out bool keyAssigned)
    {
        var values = new Dictionary<string, object?>();
        keyAssigned = false;
        foreach (var (prop, column) in map.Columns)
        {
            if (!map.TryGetValue(entity, prop, out var value)) continue;
            if (prop == map.KeyProperty)
            {
                keyAssigned = value is not null && !IsDefault(value);
                if (!keyAssigned) continue;   // let the DB generate when unassigned
            }
            values[column] = UnwrapTenantId(value);
        }
        return values;
    }

    private static object? UnwrapTenantId(object? value) => value switch
    {
        TenantId tid => tid.Value,
        _ => value
    };

    private static object ConvertKey(object value, System.Type keyType)
    {
        if (value.GetType() == keyType) return value;                       // e.g. Npgsql returns uuid as Guid, int4 as int
        if (keyType == typeof(System.Guid))
            return value is System.Guid g ? g : System.Guid.Parse(value.ToString()!);
        return System.Convert.ChangeType(value, keyType);                   // numeric widening etc.
    }

    private static bool IsDefault(object value) => value switch
    {
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        System.Guid g => g == System.Guid.Empty,
        _ => false
    };

    private sealed class TransactionScope(IDapperConnectionContext connection, DapperUnitOfWork owner) : ITransactionScope
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await CompleteAsync(tx => tx.CommitAsync(cancellationToken));
            }
            catch
            {
                // CompleteAsync disposed the transaction, so DisposeAsync below would no-op: restore here.
                owner.RestoreRecordedVersions();
                throw;
            }

            owner.EndVersionRecord();
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await CompleteAsync(tx => tx.RollbackAsync(cancellationToken));
            }
            finally
            {
                owner.RestoreRecordedVersions();
            }
        }

        // Always dispose the ambient transaction, even when commit/rollback throws, so a failed
        // commit leaves nothing for DisposeAsync to roll back (which would mask the original error).
        private async Task CompleteAsync(Func<System.Data.Common.DbTransaction, Task> complete)
        {
            if (connection.CurrentTransaction is not { } tx) return;
            try { await complete(tx); }
            finally { await connection.DisposeTransactionAsync(); }
        }

        public async ValueTask DisposeAsync()
        {
            // Safety net for a scope that was never explicitly committed/rolled back. After an
            // explicit Commit/Rollback the transaction is already disposed, so this is a no-op.
            // The rollback is best-effort: DisposeAsync must not throw and mask an in-flight exception.
            if (connection.CurrentTransaction is not { } tx) return;
            try { await tx.RollbackAsync(); }
            catch { /* best-effort cleanup; an in-flight exception takes precedence */ }
            finally
            {
                await connection.DisposeTransactionAsync();
                owner.RestoreRecordedVersions();
            }
        }
    }
}
