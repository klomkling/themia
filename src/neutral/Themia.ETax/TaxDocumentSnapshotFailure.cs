namespace Themia.ETax;

/// <summary>Why a stored snapshot could not be read.</summary>
public enum TaxDocumentSnapshotFailure
{
    /// <summary>The stored text no longer hashes to the recorded value: it was edited or rewritten by the column type.</summary>
    HashMismatch,

    /// <summary>The stored schema version is not one this release can read.</summary>
    UnsupportedSchemaVersion,

    /// <summary>The text is not a snapshot of any known shape.</summary>
    Malformed,
}
