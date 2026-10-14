namespace Themia.ETax;

/// <summary>The document totals, in satang.</summary>
public sealed record TaxDocumentTotals
{
    /// <summary>Sum of the lines before VAT.</summary>
    public required long ExVatSatang { get; init; }

    /// <summary>VAT.</summary>
    public required long VatSatang { get; init; }

    /// <summary>Total including VAT.</summary>
    public required long InclVatSatang { get; init; }

    /// <summary>Tax the customer withheld at source from this payment (0 when none).</summary>
    public required long WithheldSatang { get; init; }

    /// <summary><see cref="InclVatSatang"/> minus <see cref="WithheldSatang"/>: the cash actually received.</summary>
    public required long NetReceivedSatang { get; init; }

    /// <summary>VAT rate in basis points (700 = 7%).</summary>
    public required int VatRateBasisPoints { get; init; }
}
