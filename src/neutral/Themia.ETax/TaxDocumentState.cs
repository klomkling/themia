namespace Themia.ETax;

/// <summary>
/// Where a stored document is in its life. "In progress" is not a state: it is a lease
/// (<see cref="TaxDocumentRecord.ClaimToken"/> + <see cref="TaxDocumentRecord.ClaimExpiresAt"/>) held on an
/// <see cref="Issued"/> or <see cref="OriginalPrepared"/> document.
/// </summary>
public enum TaxDocumentState
{
    /// <summary>Stored with its number and snapshot; the ORIGINAL has not been rendered yet.</summary>
    Issued = 1,

    /// <summary>The ORIGINAL was rendered and its hash recorded, before sending. A retry re-renders the same ORIGINAL.</summary>
    OriginalPrepared = 2,

    /// <summary>The e-Tax email was accepted by the mail transport.</summary>
    Sent = 3,

    /// <summary>Automation gave up (permanent error, or too many transient ones). Terminal until <c>RequeueAsync</c>.</summary>
    Failed = 4,

    /// <summary>Replaced by a corrected document. Terminal; keeps its number and is never deleted.</summary>
    Cancelled = 5,
}
