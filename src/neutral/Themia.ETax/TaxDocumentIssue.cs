namespace Themia.ETax;

/// <summary>What to issue.</summary>
public sealed record IssueTaxDocumentRequest
{
    /// <summary>The idempotency key; <see cref="TaxDocumentKey.Kind"/> must equal the snapshot's kind.</summary>
    public required TaxDocumentKey Key { get; init; }

    /// <summary>The issuing seller (numbering scope; replacements and notes must stay within one seller).</summary>
    public required string SellerId { get; init; }

    /// <summary>
    /// Everything the document prints. Its <see cref="TaxDocumentSnapshot.DocumentNumber"/> is ignored: Issue allocates the number
    /// and writes it into the stored snapshot.
    /// </summary>
    public required TaxDocumentSnapshot Snapshot { get; init; }

    /// <summary>Where route A delivers the document; may be set later with <c>UpdateBuyerEmailAsync</c> before it is sent.</summary>
    public string? BuyerEmail { get; init; }
}

/// <summary>How <see cref="TaxDocumentService.IssueAsync"/> ended.</summary>
public enum TaxDocumentIssueOutcome
{
    /// <summary>A new document was stored. Commit the transaction.</summary>
    Issued = 0,

    /// <summary>A document already exists for the key; nothing was written and no number was consumed.</summary>
    AlreadyIssued = 1,

    /// <summary>The snapshot failed validation; see <see cref="TaxDocumentIssueResult.Errors"/>. Roll back.</summary>
    ValidationFailed = 2,

    /// <summary>A credit/debit note refers to a document that is missing, of another seller, cancelled, or whose ORIGINAL was never prepared. Roll back.</summary>
    NoteReferenceInvalid = 3,

    /// <summary>The document to replace is missing, of another seller, or already cancelled. Roll back.</summary>
    ReplacementInvalid = 4,

    /// <summary>The document to replace is being sent right now (live lease). Roll back and retry later.</summary>
    ReplacedDocumentBusy = 5,

    /// <summary>
    /// The key already belongs to ANOTHER seller's document. It is never answered with that document's id. A source event id must be globally
    /// unique across sellers, so this is a bug in the caller's id scheme. Roll back.
    /// </summary>
    SourceConflict = 6,
}

/// <summary>The result of an issue attempt. Anything other than <see cref="TaxDocumentIssueOutcome.Issued"/> and <see cref="TaxDocumentIssueOutcome.AlreadyIssued"/> means: roll the transaction back.</summary>
public sealed record TaxDocumentIssueResult
{
    /// <summary>How it ended.</summary>
    public required TaxDocumentIssueOutcome Outcome { get; init; }

    /// <summary>The new document (<see cref="TaxDocumentIssueOutcome.Issued"/>) or the existing one (<see cref="TaxDocumentIssueOutcome.AlreadyIssued"/>).</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>The failed checks, for <see cref="TaxDocumentIssueOutcome.ValidationFailed"/>: codes and field paths only.</summary>
    public IReadOnlyList<TaxDocumentValidationError> Errors { get; init; } = [];
}
