using global::Dapper;
using SqlKata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
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
    TimeProvider timeProvider) : IUnitOfWork, IPendingOperationSink
{
    private readonly List<PendingOperation> pending = [];

    public void Enqueue(PendingOperation operation) => pending.Add(operation);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (pending.Count == 0) return 0;
        var ownsTransaction = connection.CurrentTransaction is null;
        if (ownsTransaction) await connection.BeginTransactionAsync(cancellationToken);
        var conn = await connection.GetOpenConnectionAsync(cancellationToken);
        var tx = connection.CurrentTransaction;
        try
        {
            var affected = 0;
            foreach (var op in pending)
                affected += await ExecuteAsync(conn, tx, op, cancellationToken);
            if (ownsTransaction && tx is not null)
                await tx.CommitAsync(cancellationToken);
            pending.Clear();
            return affected;
        }
        catch
        {
            pending.Clear();
            if (ownsTransaction && tx is not null)
            {
                // Roll back with CancellationToken.None so a cancelled token can't abort the cleanup,
                // and swallow a rollback failure so it doesn't mask the original error (rethrown below).
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { /* preserve the original exception */ }
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
        return new TransactionScope(connection);
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
                var query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsUpdate(values);
                var sql = compiler.Compile(query);
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
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
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsUpdate(values);
                }
                else
                {
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsDelete();
                }
                var sql = compiler.Compile(query);
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
            }
            default: return 0;
        }
    }

    // Scope the write to the ambient tenant's rows; with no ambient tenant, restrict to global
    // (tenant_id IS NULL) rows so a system context cannot mutate a tenant-owned row by primary key.
    // Mirrors the read path (TenantPredicate) and EF's ValidateTenantAccess (no tenant => global only).
    private Query TenantScoped(Query q, object entity, EntityMapping map)
    {
        if (entity is ITenantEntity)
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

    private sealed class TransactionScope(IDapperConnectionContext connection) : ITransactionScope
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            CompleteAsync(tx => tx.CommitAsync(cancellationToken));

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            CompleteAsync(tx => tx.RollbackAsync(cancellationToken));

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
            finally { await connection.DisposeTransactionAsync(); }
        }
    }
}
