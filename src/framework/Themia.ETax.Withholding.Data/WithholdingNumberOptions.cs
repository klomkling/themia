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

/// <summary>The shape of a printed withholding certificate number. The running number is always 6 digits.</summary>
public enum WithholdingNumberShape
{
    /// <summary>Prefix, year, running number: <c>WHT-2569-000123</c>. The default.</summary>
    PrefixYearSeq = 0,

    /// <summary>Running number, a slash and the 2-digit year: <c>000010/67</c>.</summary>
    SeqSlashYear2 = 1,

    /// <summary>Year and running number: <c>2569-000123</c>.</summary>
    YearSeq = 2,

    /// <summary>
    /// Prefix, the 2-digit year of the era and the 2-digit month, running number: <c>WHT-6910-000001</c> for October 2569. The only shape that
    /// <see cref="TaxDocumentNumberPeriod.Monthly"/> accepts, because every other shape prints the year only and a monthly run would print the same
    /// number in two months.
    /// </summary>
    PrefixYearMonthSeq = 3,
}

/// <summary>How <see cref="SequenceWithholdingNumberSource"/> formats withholding certificate numbers.</summary>
public sealed class WithholdingNumberOptions
{
    /// <summary>The printed shape. Default <see cref="WithholdingNumberShape.PrefixYearSeq"/>. It changes the printed text only, never the run key.</summary>
    public WithholdingNumberShape Shape { get; set; } = WithholdingNumberShape.PrefixYearSeq;

    /// <summary>The prefix of <see cref="WithholdingNumberShape.PrefixYearSeq"/> and <see cref="WithholdingNumberShape.PrefixYearMonthSeq"/>. Default <c>WHT</c>. Ignored by the other shapes.</summary>
    public string Prefix { get; set; } = "WHT";

    /// <summary>
    /// When the run restarts: yearly by default. <see cref="TaxDocumentNumberPeriod.Monthly"/> is valid only with
    /// <see cref="WithholdingNumberShape.PrefixYearMonthSeq"/>; any other shape with Monthly fails at startup.
    /// </summary>
    public TaxDocumentNumberPeriod Period { get; set; } = TaxDocumentNumberPeriod.Yearly;

    /// <summary>Tenant counters (default) or host-level counters.</summary>
    public TaxDocumentNumberScope Scope { get; set; } = TaxDocumentNumberScope.Tenant;

    /// <summary>How the year is printed. Default Buddhist era; Gregorian is an option. It changes the printed text only, never the counter's key, so switching it does not restart a run.</summary>
    public WithholdingNumberYear Year { get; set; } = WithholdingNumberYear.BuddhistEra;

    internal void Validate()
    {
        if (!Enum.IsDefined(Shape) || !Enum.IsDefined(Period) || !Enum.IsDefined(Scope) || !Enum.IsDefined(Year))
            throw new InvalidOperationException($"{nameof(WithholdingNumberOptions)}: unknown shape, period, scope or year style.");
        if (Shape is WithholdingNumberShape.PrefixYearSeq or WithholdingNumberShape.PrefixYearMonthSeq && string.IsNullOrWhiteSpace(Prefix))
            throw new InvalidOperationException($"{nameof(WithholdingNumberOptions)}.{nameof(Prefix)} must not be blank for the {Shape} shape.");
        if (Period == TaxDocumentNumberPeriod.Monthly && Shape != WithholdingNumberShape.PrefixYearMonthSeq)
            throw new InvalidOperationException(
                $"{nameof(WithholdingNumberOptions)}: {nameof(Period)} Monthly needs the {nameof(WithholdingNumberShape.PrefixYearMonthSeq)} shape; the {Shape} shape prints the year only, so two months would print the same number.");
    }
}
