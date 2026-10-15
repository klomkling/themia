namespace Themia.ETax.Withholding;

/// <summary>One income row of the certificate: an income type, the date paid, the amount paid and the tax withheld.</summary>
public sealed record WithholdingIncomeRow
{
    /// <summary>The <see cref="IncomeTypeEntry.Code"/> the app selected.</summary>
    public required string IncomeTypeCode { get; init; }

    /// <summary>Copy of <see cref="IncomeTypeEntry.Section"/> at issue, so the certificate prints the same after the catalogue changes.</summary>
    public required string Section { get; init; }

    /// <summary>Copy of <see cref="IncomeTypeEntry.SubItem"/> at issue.</summary>
    public string? SubItem { get; init; }

    /// <summary>Copy of <see cref="IncomeTypeEntry.LabelTh"/> at issue.</summary>
    public required string LabelTh { get; init; }

    /// <summary>Copy of <see cref="IncomeTypeEntry.FormRow"/> at issue.</summary>
    public required int FormRow { get; init; }

    /// <summary>Free text for rows that ask the payer to specify the income (e.g. the "other" row).</summary>
    public string? Description { get; init; }

    /// <summary>The date the income was paid (stored Gregorian, printed Buddhist era). Never after the issue date.</summary>
    public required DateOnly DatePaid { get; init; }

    /// <summary>The amount paid, in satang.</summary>
    public required long AmountPaidSatang { get; init; }

    /// <summary>
    /// The rate the tax was computed at, in basis points. When set, the validator requires
    /// <see cref="TaxWithheldSatang"/> to equal <see cref="WithholdingMath.Withhold"/>; when <see langword="null"/> (a rate the
    /// package does not compute, e.g. a progressive salary table) only <c>withheld ≤ paid</c> is checked.
    /// </summary>
    public int? RateBasisPoints { get; init; }

    /// <summary>The tax withheld, in satang.</summary>
    public required long TaxWithheldSatang { get; init; }
}
