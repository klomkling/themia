namespace Themia.ETax;

/// <summary>What a credit or debit note refers to and why it was issued (s.86/9, s.86/10).</summary>
public sealed record TaxNoteReference
{
    /// <summary>Id of the referenced document.</summary>
    public required Guid OriginalDocumentId { get; init; }

    /// <summary>Number of the referenced document, as printed.</summary>
    public required string OriginalDocumentNumber { get; init; }

    /// <summary>Issue date of the referenced document.</summary>
    public required DateOnly OriginalIssueDate { get; init; }

    /// <summary>Whole-document cancellation or value adjustment.</summary>
    public required TaxNoteMode Mode { get; init; }

    /// <summary>Optional app-defined reason code.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Why the note was issued.</summary>
    public required string Reason { get; init; }

    /// <summary><see cref="TaxNoteMode.FullCancellation"/>: the cash actually refunded; equals the net received.</summary>
    public long? RefundedNetSatang { get; init; }

    /// <summary><see cref="TaxNoteMode.Adjustment"/>: the VAT-inclusive value before the correction.</summary>
    public long? OriginalValueSatang { get; init; }

    /// <summary><see cref="TaxNoteMode.Adjustment"/>: the VAT-inclusive value after the correction.</summary>
    public long? CorrectedValueSatang { get; init; }
}
