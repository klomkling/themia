namespace Themia.ETax.Withholding;

/// <summary>The lifecycle of a stored certificate. <see cref="Cancelled"/> is final.</summary>
public enum WithholdingCertificateState
{
    /// <summary>Issued and valid.</summary>
    Issued = 1,

    /// <summary>Cancelled because its content was wrong; its replacement is recorded in <see cref="WithholdingCertificateRecord.ReplacedByCertificateId"/>.</summary>
    Cancelled = 2,
}
