namespace Themia.ETax.Withholding;

/// <summary>How a certificate is rendered. Chosen when rendering, never stored.</summary>
public enum WithholdingRenderMode
{
    /// <summary>The certificate as issued.</summary>
    Issued = 0,

    /// <summary>
    /// A replacement copy for a lost or damaged certificate (ใบแทน): the same certificate and number with "ใบแทน" written at the top. It is
    /// not a new certificate and takes no number. The certifying signature is the issuer's, outside this document.
    /// </summary>
    ReplacementCopy = 1,

    /// <summary>The certificate was cancelled (replaced by a corrected one): printed with the cancelled watermark.</summary>
    Cancelled = 2,
}
