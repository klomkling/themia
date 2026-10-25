namespace Themia.ETax.Data;

/// <summary>When a document number run starts again at 1.</summary>
public enum TaxDocumentNumberPeriod
{
    /// <summary>One unbroken run per seller and kind, for ever.</summary>
    Never = 0,

    /// <summary>A new run each calendar year of the issue date (<c>TAX-2026-000001</c>).</summary>
    Yearly = 1,

    /// <summary>A new run each calendar month of the issue date (<c>TAX-202610-000001</c>).</summary>
    Monthly = 2,
}

/// <summary>How the period is written in a printed number. It never changes which run a number belongs to: the run key stays on the Gregorian period.</summary>
public enum TaxDocumentNumberYearStyle
{
    /// <summary>Gregorian year, four digits (<c>TAX-2026-000001</c>, <c>TAX-202610-000001</c>).</summary>
    Gregorian = 0,

    /// <summary>Buddhist-era year, last two digits (<c>RTX-69-000001</c> yearly, <c>RTX-6910-000001</c> monthly).</summary>
    BuddhistEraTwoDigit = 1,
}

/// <summary>Whose counters the numbers come from.</summary>
public enum TaxDocumentNumberScope
{
    /// <summary>The ambient tenant's counters (the allocator throws if there is no tenant).</summary>
    Tenant = 0,

    /// <summary>Host-level counters outside any tenant.</summary>
    Host = 1,
}

/// <summary>How <see cref="SequenceTaxDocumentNumberSource"/> forms numbers. Anything fancier is an app's own <see cref="ITaxDocumentNumberSource"/>.</summary>
public sealed class TaxDocumentNumberOptions
{
    /// <summary>The longest <c>themia_sequences</c> key (the allocator's own column limit).</summary>
    internal const int MaxKeyLength = 100;

    /// <summary>Prefix per kind. Defaults: TAX, CN, DN.</summary>
    public IDictionary<TaxDocumentKind, string> Prefixes { get; } = new Dictionary<TaxDocumentKind, string>
    {
        [TaxDocumentKind.TaxInvoiceReceipt] = "TAX",
        [TaxDocumentKind.CreditNote] = "CN",
        [TaxDocumentKind.DebitNote] = "DN",
    };

    /// <summary>When the run restarts. Default <see cref="TaxDocumentNumberPeriod.Yearly"/>.</summary>
    public TaxDocumentNumberPeriod Period { get; set; } = TaxDocumentNumberPeriod.Yearly;

    /// <summary>
    /// How the year is printed. Default <see cref="TaxDocumentNumberYearStyle.Gregorian"/>, so numbers already issued keep their shape.
    /// <see cref="TaxDocumentNumberYearStyle.BuddhistEraTwoDigit"/> with <see cref="TaxDocumentNumberPeriod.Monthly"/> gives <c>RTX-6910-000001</c>
    /// (the prefix is the adopter's, see <see cref="Prefixes"/>).
    /// </summary>
    public TaxDocumentNumberYearStyle YearStyle { get; set; } = TaxDocumentNumberYearStyle.Gregorian;

    /// <summary>Zero padding of the counter. Default 6.</summary>
    public int Padding { get; set; } = 6;

    /// <summary>Tenant or host counters. Default <see cref="TaxDocumentNumberScope.Tenant"/>.</summary>
    public TaxDocumentNumberScope Scope { get; set; } = TaxDocumentNumberScope.Tenant;

    internal void Validate()
    {
        if (Padding is < 1 or > 18)
            throw new InvalidOperationException($"{nameof(TaxDocumentNumberOptions)}.{nameof(Padding)} must be between 1 and 18.");
        if (!Enum.IsDefined(Period) || !Enum.IsDefined(Scope) || !Enum.IsDefined(YearStyle))
            throw new InvalidOperationException($"{nameof(TaxDocumentNumberOptions)}: unknown period, scope or year style.");
        foreach (var kind in Enum.GetValues<TaxDocumentKind>())
        {
            if (!Prefixes.TryGetValue(kind, out var prefix) || string.IsNullOrWhiteSpace(prefix))
                throw new InvalidOperationException($"{nameof(TaxDocumentNumberOptions)}.{nameof(Prefixes)} has no prefix for {kind}.");
        }
    }
}
