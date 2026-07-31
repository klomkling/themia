using System.Data.Common;

namespace Themia.Messaging.Inbox;

/// <summary>
/// Engine-specific insert-if-not-exists for inbox admission. Takes the caller's connection and
/// transaction rather than opening its own: admission must commit with the application's state change,
/// or a crash between the two loses the message permanently.
/// </summary>
public interface IInboxAdmissionDialect
{
    /// <summary>
    /// Attempts to record the message as admitted, in ONE statement. A read-then-write would let two
    /// concurrent deliveries of the same message both observe "not seen" and both process it.
    /// </summary>
    /// <param name="connection">The caller's open connection.</param>
    /// <param name="transaction">The caller's ambient transaction, if any.</param>
    /// <param name="origin">The system that originated the message.</param>
    /// <param name="messageId">The sender's stable message identifier.</param>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/>.</param>
    /// <param name="type">The logical message type, recorded for diagnostics.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when this call inserted the record; <see langword="false"/> when it already existed.</returns>
    Task<bool> TryAdmitAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string origin,
        Guid messageId,
        string? tenantId,
        string type,
        CancellationToken ct);
}
