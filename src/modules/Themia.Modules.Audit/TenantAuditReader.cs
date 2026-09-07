using Themia.Audit;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Audit;

/// <summary>Default <see cref="ITenantAuditReader"/>: opens its own connection via <see cref="IAuditDialect"/>
/// and forces every <see cref="AuditQuery.TenantId"/> to the ambient tenant before delegating to
/// <see cref="IAuditStore.QueryAsync"/>.</summary>
public sealed class TenantAuditReader : ITenantAuditReader
{
    private readonly IAuditStore store;
    private readonly IAuditDialect dialect;
    private readonly AuditOptions options;
    private readonly ITenantContext tenantContext;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">The store queried for the (already tenant-scoped) page.</param>
    /// <param name="dialect">Opens the connection the query runs on.</param>
    /// <param name="options">Supplies the connection string <paramref name="dialect"/> opens against.</param>
    /// <param name="tenantContext">Supplies the tenant every query is forced to.</param>
    public TenantAuditReader(IAuditStore store, IAuditDialect dialect, AuditOptions options, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantContext);

        this.store = store;
        this.dialect = dialect;
        this.options = options;
        this.tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (tenantContext.CurrentTenantId is null)
        {
            // A null tenant is "no filter" to AuditQuery/IAuditStore.QueryAsync (design §9), which would
            // make this tenant-scoped reader fail open to every tenant's rows the moment there is no
            // ambient tenant — the default DI state for any host without tenant infrastructure. A caller
            // reaching for ITenantAuditReader has stated an intent this class cannot honour without one.
            throw new InvalidOperationException(
                $"{nameof(ITenantAuditReader)} requires an ambient tenant and cannot be used to read across " +
                $"tenants. Use {nameof(IAuditStore)}.{nameof(IAuditStore.QueryAsync)} directly for the " +
                "deliberate cross-tenant/host-level path.");
        }

        // Overrides whatever the caller supplied — a query cannot be satisfied for another tenant, even
        // when it explicitly names one (design §9).
        var scoped = query with { TenantId = tenantContext.CurrentTenantId.Value.Value };

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await store.QueryAsync(scoped, connection, cancellationToken).ConfigureAwait(false);
    }
}
