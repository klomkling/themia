namespace Themia.Messaging.Outbox;

/// <summary>
/// The columns the drainer itself needs from a claimed outbox row. Everything else a row carries is
/// payload the <see cref="IOutboxDispatcher{TRow}"/> understands and the drainer never inspects, which
/// is what lets one drainer serve outboxes with different schemas.
/// </summary>
public interface IClaimedRow
{
    /// <summary>The outbox row primary key.</summary>
    Guid Id { get; }

    /// <summary>The number of delivery attempts already made before this claim.</summary>
    int Attempts { get; }
}
