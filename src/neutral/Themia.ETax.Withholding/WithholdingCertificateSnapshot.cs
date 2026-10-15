namespace Themia.ETax.Withholding;

/// <summary>
/// Everything a withholding tax certificate (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ) prints, frozen at issue. Money is in satang,
/// rates in basis points. Persist it through <see cref="WithholdingSnapshotSerializer"/>; the template version lives in its own column.
/// </summary>
public sealed record WithholdingCertificateSnapshot
{
    /// <summary>The current schema version of the persisted JSON.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of this snapshot.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The certificate number as printed (เลขที่).</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The book number as printed (เล่มที่), if the payer uses one.</summary>
    public string? BookNumber { get; init; }

    /// <summary>The date of issue, supplied by the caller (the Bangkok business date), never derived from UTC.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The withholder (ผู้มีหน้าที่หักภาษี ณ ที่จ่าย). Tax id, branch and address are required.</summary>
    public required TaxParty Payer { get; init; }

    /// <summary>The person withheld from (ผู้ถูกหักภาษี). Tax id and address are required.</summary>
    public required TaxParty Payee { get; init; }

    /// <summary>Whether the payee is a natural or a juristic person.</summary>
    public required WithholdingPayeeKind PayeeKind { get; init; }

    /// <summary>The return the certificate belongs to.</summary>
    public required WithholdingFilingForm FilingForm { get; init; }

    /// <summary>The line number in that return (ลำดับที่ในแบบ), if known at issue.</summary>
    public int? FilingSequence { get; init; }

    /// <summary>The <see cref="IncomeTypeCatalogue.Version"/> the rows were selected from.</summary>
    public required string IncomeCatalogueVersion { get; init; }

    /// <summary>The income rows. At least one.</summary>
    public required IReadOnlyList<WithholdingIncomeRow> Rows { get; init; }

    /// <summary>The totals of <see cref="Rows"/>.</summary>
    public required WithholdingTotals Totals { get; init; }

    /// <summary>Contributions printed under the totals, if any.</summary>
    public WithholdingFunds? Funds { get; init; }

    /// <summary>How the tax was borne.</summary>
    public required WithholdingCondition Condition { get; init; }

    /// <summary>The description when <see cref="Condition"/> is <see cref="WithholdingCondition.Other"/>; otherwise <see langword="null"/>.</summary>
    public string? ConditionOther { get; init; }

    /// <summary>Set when this certificate replaces a cancelled one.</summary>
    public TaxDocumentReplacement? Replacement { get; init; }
}
