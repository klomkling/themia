using System.Linq.Expressions;
using global::Dapper;
using SqlKata;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Translation;
using Themia.Framework.Data.Dapper.UnitOfWork;

namespace Themia.Framework.Data.Dapper.Repositories;

internal sealed class DapperRepository<T, TKey>(
    IDapperConnectionContext connection,
    ITenantContext tenantContext,
    IDataFilterScope filterScope,
    DapperDataOptions options,
    EntityMappingRegistry registry,
    ISqlCompiler compiler,
    IPendingOperationSink sink)
    : DapperReadRepository<T, TKey>(connection, tenantContext, filterScope, options, registry, compiler), IRepository<T, TKey> where T : class
{
    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        sink.Enqueue(new PendingOperation(PendingKind.Add, entity, typeof(T)));
        return Task.CompletedTask;
    }

    public void Update(T entity) => sink.Enqueue(new PendingOperation(PendingKind.Update, entity, typeof(T)));

    public void Remove(T entity) => sink.Enqueue(new PendingOperation(PendingKind.Remove, entity, typeof(T)));

    public async Task<int> UpdateWhereAsync(
        ISpecification<T> specification,
        Action<IBulkUpdateSetters<T>> set,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(set);

        // Seed the same WHERE the read path uses for this spec — the tenant predicate + soft-delete filter
        // (with ignoreTenantFilter honoured) plus the spec's criteria — so tenant isolation is part of the
        // emitted UPDATE by construction. Ordering/paging are dropped (meaningless for an UPDATE).
        var query = Seeded(specification.IgnoreTenantFilter);
        SpecificationTranslator.ApplyCriteria(query, specification, Map);

        var setters = new DapperBulkUpdateSetters(Map);
        set(setters);
        if (setters.Values.Count == 0)
            throw new InvalidOperationException("UpdateWhereAsync requires at least one Set(...) call.");

        var sql = Compiler.Compile(query.AsUpdate(setters.Values));
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        // Run on the shared connection, joining the ambient transaction when one is open (CurrentTransaction
        // is null when none is) so the bulk update commits/rolls back with the surrounding unit of work.
        return await conn.ExecuteAsync(
            new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
    }

    // Resolves each Set(property, value) to a column via the entity mapping, collecting the SET clause.
    private sealed class DapperBulkUpdateSetters(EntityMapping map) : IBulkUpdateSetters<T>
    {
        public Dictionary<string, object?> Values { get; } = [];

        public IBulkUpdateSetters<T> Set<TProperty>(Expression<Func<T, TProperty>> property, TProperty value)
        {
            // Shared helper so an invalid setter expression fails identically across every peer.
            Values[map.Column(BulkUpdateSetters.MemberName(property))] = value;
            return this;
        }
    }
}
