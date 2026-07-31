using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Messaging.Inbox;

namespace Themia.Modules.Messaging.Inbox;

/// <summary>
/// Dapper-peer <see cref="IInboxStore"/>. Runs on the caller's ambient connection and transaction so the
/// admission record and the application's state change commit together — the whole point of the inbox.
/// </summary>
/// <remarks>
/// This is the sanctioned data-layer raw-connection path. There is deliberately no EF implementation:
/// <c>Themia.Framework.Data.EFCore</c> exposes no connection or transaction access, and a version that
/// opened its own connection would reintroduce the loss window it exists to close.
/// </remarks>
internal sealed class DapperInboxStore(
    IDapperConnectionContext connectionContext,
    IInboxAdmissionDialect dialect,
    ITenantContext tenantContext) : IInboxStore
{
    /// <inheritdoc />
    public async Task<InboxAdmission> TryAdmitAsync(
        string origin, Guid messageId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (connectionContext.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Inbox admission requires an ambient transaction: TryAdmitAsync must commit together with the "
                + "caller's state change, or a crash between the two can drop a message permanently while "
                + "looking like correct deduplication. Begin a unit-of-work transaction (IUnitOfWork."
                + "BeginTransactionAsync) before calling TryAdmitAsync, and commit it after applying the "
                + "message's effect.");
        }

#pragma warning disable THEMIA103 // Deliberate bypass: inbox admission is keyed on (origin, message id), not
        // tenant, by design — TryAdmitAsync passes tenantId through as an explicit, recorded column rather than
        // a query-level tenant filter, and IInboxAdmissionDialect.TryAdmitAsync is the one sanctioned insert
        // this store issues. There is no ITenantQueryFactory path for a single-statement insert-if-not-exists.
        var connection = await connectionContext.GetOpenConnectionAsync(ct).ConfigureAwait(false);
#pragma warning restore THEMIA103

        var inserted = await dialect.TryAdmitAsync(
            connection,
            connectionContext.CurrentTransaction,
            origin,
            messageId,
            tenantContext.CurrentTenantId?.Value,
            type,
            ct).ConfigureAwait(false);

        return inserted ? InboxAdmission.Accepted : InboxAdmission.Duplicate;
    }
}
