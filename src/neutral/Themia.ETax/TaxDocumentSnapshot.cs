namespace Themia.ETax;

/// <summary>
/// Everything a tax document prints, frozen at issue. Rendering reads only this and the pinned template
/// version, never today's policy, so a later copy matches the original except for its marker.
/// </summary>
/// <remarks>
/// Persisted as one text column through <see cref="TaxDocumentSnapshotSerializer"/>; do not mutate a stored one.
/// </remarks>
public sealed record TaxDocumentSnapshot
{
    /// <summary>The schema version written by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the persisted shape.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Document kind.</summary>
    public required TaxDocumentKind Kind { get; init; }

    /// <summary>The allocated gapless document number.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The tax point: the caller-supplied business date (Bangkok) of the payment or refund, never derived from UTC.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The seller.</summary>
    public required TaxParty Seller { get; init; }

    /// <summary>The buyer.</summary>
    public required TaxParty Buyer { get; init; }

    /// <summary>At least one line.</summary>
    public required IReadOnlyList<TaxDocumentLine> Lines { get; init; }

    /// <summary>Totals.</summary>
    public required TaxDocumentTotals Totals { get; init; }

    /// <summary>Required for credit and debit notes, forbidden otherwise.</summary>
    public TaxNoteReference? Note { get; init; }

    /// <summary>Present when this document replaces a cancelled one.</summary>
    public TaxDocumentReplacement? Replacement { get; init; }

    /// <summary>How and when a receipt was paid. Receipts only; omitted from the stored JSON when <see langword="null"/>.</summary>
    public TaxPayment? Payment { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 of the logo the document was issued with (paged templates only). Stamped by <see cref="TaxDocumentService"/>; a caller-supplied value
    /// is rejected. Omitted from the stored JSON when <see langword="null"/>, so a document without a logo is byte-identical to one issued before logos existed.
    /// </summary>
    public string? LogoSha256 { get; init; }
}
