namespace Themia.ETax.Withholding;

/// <summary>
/// The idempotency key of an issued certificate: at most one certificate exists per (<see cref="SourceEventId"/>, <see cref="Revision"/>).
/// </summary>
/// <remarks>
/// <see cref="SourceEventId"/> is the <b>triggering event</b>, typically the payout or payment the tax was withheld from. A replacement
/// for a cancelled certificate keeps the source event and takes the next revision. The key carries no payer, so
/// <see cref="SourceEventId"/> must be globally unique across payers (one id space). If two payers ever issue under the same key,
/// the second gets <see cref="WithholdingIssueOutcome.SourceConflict"/>, never the other payer's certificate id.
/// </remarks>
public readonly record struct WithholdingCertificateKey
{
    /// <summary>Creates a key.</summary>
    /// <param name="sourceEventId">The triggering event's id.</param>
    /// <param name="revision">1 for the first certificate of the event; a replacement takes the next number.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceEventId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="revision"/> is below 1.</exception>
    public WithholdingCertificateKey(string sourceEventId, int revision = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        SourceEventId = sourceEventId;
        Revision = revision;
    }

    /// <summary>The triggering event's id.</summary>
    public string SourceEventId { get; }

    /// <summary>1 for the first certificate of the event; a replacement takes the next number.</summary>
    public int Revision { get; }
}
