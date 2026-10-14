namespace Themia.ETax;

/// <summary>
/// The idempotency key of an issued document: at most one document exists per
/// (<see cref="Kind"/>, <see cref="SourceEventId"/>, <see cref="Revision"/>).
/// </summary>
/// <remarks>
/// <see cref="SourceEventId"/> is the <b>triggering event</b>, not the payment: a tax invoice/receipt is
/// keyed by its payment id, a credit note by its refund id, a debit note by its own event id. Keying a credit
/// note by the payment would make the second partial refund on one payment look like a duplicate of the first.
/// A replacement for a cancelled document keeps the source event and takes the next revision.
/// </remarks>
public readonly record struct TaxDocumentKey
{
    /// <summary>Creates a key.</summary>
    /// <param name="kind">The document kind.</param>
    /// <param name="sourceEventId">The triggering event's id (payment id, refund id, debit-note event id).</param>
    /// <param name="revision">1 for the first document of the event; a replacement takes the next number.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceEventId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is undefined or <paramref name="revision"/> is below 1.</exception>
    public TaxDocumentKey(TaxDocumentKind kind, string sourceEventId, int revision = 1)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        Kind = kind;
        SourceEventId = sourceEventId;
        Revision = revision;
    }

    /// <summary>The document kind.</summary>
    public TaxDocumentKind Kind { get; }

    /// <summary>The triggering event's id.</summary>
    public string SourceEventId { get; }

    /// <summary>1 for the first document of the event; a replacement takes the next number.</summary>
    public int Revision { get; }
}
