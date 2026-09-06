using Themia.Audit;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Connections;

namespace Themia.Modules.Audit;

/// <summary>
/// The transaction-aware <see cref="IAuditRecorder"/> (design §7): resolves the ambient tenant, applies
/// <see cref="AuditTransactionPolicy"/> per <see cref="AuditCategory"/>, and delegates every write to the
/// neutral <see cref="AuditRecorder"/> — never to <see cref="IAuditStore"/> directly, so validation,
/// normalization, and redaction always run.
/// </summary>
public sealed class TransactionalAuditRecorder : IAuditRecorder
{
    private readonly AuditRecorder inner;
    private readonly IAmbientConnectionAccessor connectionAccessor;
    private readonly ITenantContext tenantContext;
    private readonly AuditModuleOptions options;

    /// <summary>Creates the recorder.</summary>
    /// <param name="inner">The neutral recorder every write funnels through.</param>
    /// <param name="connectionAccessor">Resolves the caller's ambient connection and transaction, if any.</param>
    /// <param name="tenantContext">Resolves the ambient tenant when an entry leaves <see cref="AuditEntry.TenantId"/> null.</param>
    /// <param name="options">Supplies the configurable policy for <see cref="AuditCategory.Activity"/>.</param>
    public TransactionalAuditRecorder(
        AuditRecorder inner, IAmbientConnectionAccessor connectionAccessor, ITenantContext tenantContext, AuditModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connectionAccessor);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(options);

        this.inner = inner;
        this.connectionAccessor = connectionAccessor;
        this.tenantContext = tenantContext;
        this.options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never invents a tenant (design §9): when <paramref name="entry"/> already names one it is left
    /// alone; when it does not, the ambient <see cref="ITenantContext.CurrentTenantId"/> is used, which is
    /// <see langword="null"/> for a host-level event.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The resolved policy is <see cref="AuditTransactionPolicy.RequireTransaction"/> and no ambient
    /// transaction is open. The message names <c>IUnitOfWork.ExecuteInTransactionAsync</c> as the fix.
    /// </exception>
    public async ValueTask<Guid> RecordAsync(AuditEntry entry, object? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var withTenant = entry.TenantId is null
            ? entry with { TenantId = tenantContext.CurrentTenantId?.Value }
            : entry;

        var policy = ResolvePolicy(withTenant.Category);
        var ambient = await connectionAccessor.TryGetAsync(cancellationToken).ConfigureAwait(false);

        if (policy == AuditTransactionPolicy.RequireTransaction && ambient is null)
        {
            throw new InvalidOperationException(
                $"Recording an audit entry of category '{withTenant.Category}' requires an ambient " +
                "transaction, but none is open. Wrap the call in IUnitOfWork.ExecuteInTransactionAsync so " +
                "the audit row commits or rolls back atomically with the business write it documents.");
        }

        // RequireTransaction (with an ambient transaction present) and JoinIfPresent (when one happens to
        // be open) both enlist. Never ignores any ambient transaction unconditionally, and JoinIfPresent
        // with no ambient transaction falls through to the caller's-own-connection path below.
        if (ambient is { } connectionAndTransaction
            && policy is AuditTransactionPolicy.RequireTransaction or AuditTransactionPolicy.JoinIfPresent)
        {
            return await inner.RecordOnAsync(
                    withTenant, payload, connectionAndTransaction.Connection, connectionAndTransaction.Transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        return await inner.RecordAsync(withTenant, payload, cancellationToken).ConfigureAwait(false);
    }

    // Activity is the only adopter-configurable category (design §7's table); Authentication and
    // UserLifecycle are hardwired to Never because a failed login has no transaction to join and must be
    // recorded even when the surrounding request rolls back.
    private AuditTransactionPolicy ResolvePolicy(AuditCategory category) => category switch
    {
        AuditCategory.Activity => options.ActivityPolicy,
        _ => AuditTransactionPolicy.Never,
    };
}
