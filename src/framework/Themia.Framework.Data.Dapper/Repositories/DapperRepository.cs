using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;
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
}
