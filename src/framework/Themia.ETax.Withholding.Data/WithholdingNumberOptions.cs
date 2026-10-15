using Themia.ETax.Data;

namespace Themia.ETax.Withholding.Data;

/// <summary>How the printed year of a withholding certificate number is written. The counter's key always uses the Gregorian year.</summary>
public enum WithholdingNumberYear
{
    /// <summary>Buddhist era (พ.ศ.): 2026 prints as 2569.</summary>
    BuddhistEra = 0,

    /// <summary>Gregorian (ค.ศ.): 2026 prints as 2026.</summary>
    Gregorian = 1,
}

/// <summary>How <see cref="SequenceWithholdingNumberSource"/> formats withholding certificate numbers.</summary>
public sealed class WithholdingNumberOptions
{
    /// <summary>The prefix. Default <c>WHT</c>.</summary>
    public string Prefix { get; set; } = "WHT";

    /// <summary>When the run restarts: yearly by default.</summary>
    public TaxDocumentNumberPeriod Period { get; set; } = TaxDocumentNumberPeriod.Yearly;

    /// <summary>Minimum digits of the counter, zero-padded. Default 6.</summary>
    public int Padding { get; set; } = 6;

    /// <summary>Tenant counters (default) or host-level counters.</summary>
    public TaxDocumentNumberScope Scope { get; set; } = TaxDocumentNumberScope.Tenant;

    /// <summary>How the year is printed. Default Buddhist era. It changes the printed text only, never the counter's key, so switching it does not restart a run.</summary>
    public WithholdingNumberYear Year { get; set; } = WithholdingNumberYear.BuddhistEra;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prefix))
            throw new InvalidOperationException($"{nameof(WithholdingNumberOptions)}.{nameof(Prefix)} must not be blank.");
        if (Padding is < 1 or > 18)
            throw new InvalidOperationException($"{nameof(WithholdingNumberOptions)}.{nameof(Padding)} must be between 1 and 18.");
        if (!Enum.IsDefined(Period) || !Enum.IsDefined(Scope) || !Enum.IsDefined(Year))
            throw new InvalidOperationException($"{nameof(WithholdingNumberOptions)}: unknown period, scope or year style.");
    }
}
