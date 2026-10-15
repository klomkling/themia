namespace Themia.ETax.Withholding;

/// <summary>The certificate totals, in satang. Recomputed and checked by the validator, never trusted.</summary>
public sealed record WithholdingTotals
{
    /// <summary>Sum of the amounts paid.</summary>
    public required long PaidSatang { get; init; }

    /// <summary>Sum of the tax withheld.</summary>
    public required long WithheldSatang { get; init; }
}
