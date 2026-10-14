namespace Themia.ETax;

/// <summary>Options for <see cref="TaxDocumentSnapshotValidator"/>.</summary>
public sealed class TaxDocumentValidationOptions
{
    /// <summary>
    /// Check the mod-11 digit of tax ids on NEW input. Default <see langword="true"/>. An app whose stored
    /// profiles were only ever checked for 13 digits turns this off until its own rollout. It applies to
    /// issuing only: stored profiles and already-issued documents are never re-validated.
    /// </summary>
    public bool ValidateTaxIdChecksum { get; set; } = true;
}
