using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.Specifications;

/// <summary>The object with the given logical key within the ambient tenant. The predicate filters by the
/// ambient tenant at the QUERY level (not just the framework tenant filter): EF defaults
/// <c>IncludeGlobalRecordsForTenants = true</c>, so without an explicit tenant predicate a tenant query
/// also returns platform (<c>tenant_id IS NULL</c>) rows and a platform row sharing the key could shadow
/// the tenant's own row.</summary>
internal sealed class StorageObjectByKeySpec : Specification<StorageObject>
{
    public StorageObjectByKeySpec(string key, TenantId? tenantId, bool committedOnly)
    {
        // Build the predicate by branch (not with a captured `committedOnly` flag inside the expression
        // tree): the Dapper specification translator only supports entity-column predicates, so a captured
        // local boolean would fail to translate. committedOnly: pending presigned reservations
        // (CommittedAt == null) are invisible to reads; the reserve/complete path passes false to see them.
        if (committedOnly)
        {
            Where(o => o.Key == key && o.TenantId == tenantId && o.CommittedAt != null);
        }
        else
        {
            Where(o => o.Key == key && o.TenantId == tenantId);
        }
    }
}

/// <summary>All objects in the ambient tenant (soft-deleted rows are excluded by the framework filter).
/// Used to sum current usage for the per-tenant quota check. Filters by the ambient tenant at the QUERY
/// level so platform (<c>tenant_id IS NULL</c>) bytes never count against a tenant's quota.</summary>
internal sealed class AllStorageObjectsSpec : Specification<StorageObject>
{
    public AllStorageObjectsSpec(TenantId? tenantId) => Where(o => o.TenantId == tenantId);
}
