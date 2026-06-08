using SqlKata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Tenancy;

internal static class TenantPredicate
{
    public static void Apply<T>(
        Query query,
        TenantId? tenant,
        bool includeGlobalRecords,
        bool bypassTenantFilter,
        EntityMapping map)
    {
        if (typeof(ITenantEntity).IsAssignableFrom(typeof(T)) && !bypassTenantFilter)
        {
            var column = map.Column(nameof(ITenantEntity.TenantId));
            if (tenant is { } t)
            {
                if (includeGlobalRecords)
                    query.Where(q => q.Where(column, t.Value).OrWhereNull(column));
                else
                    query.Where(column, t.Value);
            }
            else
            {
                query.WhereNull(column);
            }
        }

        if (typeof(ISoftDeletable).IsAssignableFrom(typeof(T)))
            query.Where(map.Column(nameof(ISoftDeletable.IsDeleted)), false);
    }
}
