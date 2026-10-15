namespace Themia.ETax.Withholding;

/// <summary>One income type an app can select: what the form prints for it and where it may be filed.</summary>
/// <remarks>
/// Carries no rate: which rate applies to a payment is the app's business rule. A certificate stores a copy of
/// <see cref="Section"/>, <see cref="SubItem"/>, <see cref="LabelTh"/> and <see cref="FormRow"/>, so it prints the same after the
/// catalogue is replaced by a newer version.
/// </remarks>
public sealed record IncomeTypeEntry
{
    /// <summary>The stable key the app stores: lowercase letters, digits and hyphens.</summary>
    public required string Code { get; init; }

    /// <summary>The section of the Revenue Code income class as printed, e.g. <c>40(2)</c>.</summary>
    public required string Section { get; init; }

    /// <summary>The sub-item within the section as printed, if any.</summary>
    public string? SubItem { get; init; }

    /// <summary>The label as printed on the certificate.</summary>
    public required string LabelTh { get; init; }

    /// <summary>The numbered row of the income table on the certificate (1-based).</summary>
    public required int FormRow { get; init; }

    /// <summary>The returns this income type may be filed in.</summary>
    public required IReadOnlyList<WithholdingFilingForm> AllowedFilingForms { get; init; }

    /// <summary>The kinds of payee this income type may be paid to.</summary>
    public required IReadOnlyList<WithholdingPayeeKind> AllowedPayeeKinds { get; init; }
}
