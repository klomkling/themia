namespace Themia.ETax;

/// <summary>One line of the document.</summary>
public sealed record TaxDocumentLine
{
    /// <summary>What is being charged.</summary>
    public required string Description { get; init; }

    /// <summary>The amount before VAT, in satang. Never negative; corrections are credit/debit notes.</summary>
    public required long AmountExVatSatang { get; init; }

    /// <summary>How many units are charged. Printed on the A5 receipt; needs <see cref="UnitPriceSatang"/> and must be above zero.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>The unit the quantity counts (ชิ้น, ชั่วโมง), printed after the quantity.</summary>
    public string? Unit { get; init; }

    /// <summary>The price of one unit before VAT, in satang. Needs <see cref="Quantity"/>; never negative.</summary>
    public long? UnitPriceSatang { get; init; }

    /// <summary>Credit and debit notes: this line's amount before VAT on the original document, in satang. Needs <see cref="CorrectAmountExVatSatang"/>.</summary>
    public long? OriginalAmountExVatSatang { get; init; }

    /// <summary>Credit and debit notes: this line's correct amount before VAT, in satang. Needs <see cref="OriginalAmountExVatSatang"/>.</summary>
    public long? CorrectAmountExVatSatang { get; init; }
}
