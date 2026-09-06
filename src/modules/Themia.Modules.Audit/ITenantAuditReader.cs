using Themia.Audit;

namespace Themia.Modules.Audit;

/// <summary>
/// Reads <c>themia_audit_events</c> pre-scoped to the ambient tenant (design §9). The analogue of
/// <c>ITenantQueryFactory.For&lt;T&gt;()</c> from DECISION #6: the safe path is the one with the tenant
/// predicate already applied. Adopter code should read through this rather than
/// <see cref="IAuditStore.QueryAsync"/> directly, which applies no tenant predicate unless the caller's
/// <see cref="AuditQuery"/> supplies one.
/// </summary>
public interface ITenantAuditReader
{
    /// <summary>
    /// Returns a filtered, paged page of audit events, with <see cref="AuditQuery.TenantId"/> forced to
    /// the ambient tenant regardless of what <paramref name="query"/> named — a caller cannot be answered
    /// with another tenant's rows even by asking for them explicitly.
    /// </summary>
    /// <param name="query">The filter and paging to apply, minus tenant scoping.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
}
