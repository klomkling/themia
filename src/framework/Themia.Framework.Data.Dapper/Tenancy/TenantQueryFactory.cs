using SqlKata;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Tenancy;

internal sealed class TenantQueryFactory(
    EntityMappingRegistry registry,
    ITenantContext tenantContext,
    IDataFilterScope filterScope,
    DapperDataOptions options) : ITenantQueryFactory
{
    public Query For<T>()
    {
        var map = registry.For<T>();
        var query = new Query(map.Table);
        TenantPredicate.Apply<T>(
            query,
            tenantContext.CurrentTenantId,
            options.IncludeGlobalRecordsForTenants,
            filterScope.IsTenantFilterBypassed,
            map);
        return query;
    }
}
