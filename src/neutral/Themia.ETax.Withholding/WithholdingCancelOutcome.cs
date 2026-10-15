namespace Themia.ETax.Withholding;

/// <summary>The result of <see cref="IWithholdingCertificateStore.CancelForReplacementAsync"/>.</summary>
public enum WithholdingCancelOutcome
{
    /// <summary>The certificate is now Cancelled and points at its replacement.</summary>
    Cancelled = 0,

    /// <summary>No such certificate for this payer (another payer's certificate answers the same).</summary>
    NotFound = 1,

    /// <summary>It was already Cancelled; Cancelled is final and its replacement is write-once.</summary>
    AlreadyCancelled = 2,
}
