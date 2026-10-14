namespace Themia.ETax;

/// <summary>One line of the document.</summary>
public sealed record TaxDocumentLine
{
    /// <summary>What is being charged.</summary>
    public required string Description { get; init; }

    /// <summary>The amount before VAT, in satang. Never negative; corrections are credit/debit notes.</summary>
    public required long AmountExVatSatang { get; init; }
}
