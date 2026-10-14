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
}
