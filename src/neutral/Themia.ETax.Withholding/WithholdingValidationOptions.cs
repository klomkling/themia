namespace Themia.ETax.Withholding;

/// <summary>Options for <see cref="WithholdingSnapshotValidator"/>.</summary>
public sealed class WithholdingValidationOptions
{
    /// <summary>
    /// Check the mod-11 digit of tax ids on NEW input. Default <see langword="true"/>. It applies to issuing only: stored profiles
    /// and already-issued certificates are never re-validated.
    /// </summary>
    public bool ValidateTaxIdChecksum { get; set; } = true;
}
