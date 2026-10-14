namespace Themia.ETax;

/// <summary>
/// A stored snapshot failed integrity or shape checks. The message never contains snapshot content: it holds
/// names and tax ids.
/// </summary>
public sealed class TaxDocumentSnapshotException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What failed.</param>
    /// <param name="innerException">The parse failure, if any.</param>
    public TaxDocumentSnapshotException(TaxDocumentSnapshotFailure failure, Exception? innerException = null)
        : base($"The stored tax document snapshot cannot be used: {failure}.", innerException)
    {
        Failure = failure;
    }

    /// <summary>What failed.</summary>
    public TaxDocumentSnapshotFailure Failure { get; }
}
