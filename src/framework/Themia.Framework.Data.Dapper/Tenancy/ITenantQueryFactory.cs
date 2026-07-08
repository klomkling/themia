using SqlKata;

namespace Themia.Framework.Data.Dapper.Tenancy;

/// <summary>
/// Tier-2 entry point: a SqlKata <see cref="Query"/> pre-seeded with the tenant predicate +
/// soft-delete filter. Compose joins/filters on it, then execute via Dapper.
/// </summary>
public interface ITenantQueryFactory
{
    /// <summary>Returns a tenant- and soft-delete-seeded query for <typeparamref name="T"/>.</summary>
    Query For<T>();

    /// <summary>
    /// Returns a tenant- and soft-delete-seeded query for <typeparamref name="T"/>, with a per-query
    /// override of the global-records inclusion setting.
    /// </summary>
    /// <param name="includeGlobalRecords">
    /// Whether to include records where <c>tenant_id IS NULL</c>. Overrides <see cref="DapperDataOptions.IncludeGlobalRecordsForTenants"/>.
    /// This flag only has effect when there IS an ambient tenant: with no ambient tenant the predicate
    /// always restricts to <c>tenant_id IS NULL</c> (global rows), so the flag is a no-op in that case
    /// (see <c>TenantPredicate.Apply</c>).
    /// </param>
    Query For<T>(bool includeGlobalRecords);
}
