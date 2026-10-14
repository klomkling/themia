namespace Themia.ETax;

/// <summary>Marks a document as the replacement of one that was cancelled because its content was wrong.</summary>
public sealed record TaxDocumentReplacement
{
    /// <summary>Id of the cancelled document.</summary>
    public required Guid ReplacesDocumentId { get; init; }

    /// <summary>Number of the cancelled document, as printed.</summary>
    public required string OriginalDocumentNumber { get; init; }

    /// <summary>Issue date of the cancelled document.</summary>
    public required DateOnly OriginalIssueDate { get; init; }

    /// <summary>Why the original was cancelled.</summary>
    public required string Reason { get; init; }
}
