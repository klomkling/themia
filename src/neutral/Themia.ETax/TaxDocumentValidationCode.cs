namespace Themia.ETax;

/// <summary>Why a field failed validation. Codes only: the values are names and tax ids and are never echoed.</summary>
public enum TaxDocumentValidationCode
{
    /// <summary>A required value is missing or blank.</summary>
    Required,

    /// <summary>The value has the wrong shape (digits, length) or is out of range.</summary>
    Malformed,

    /// <summary>The tax id is well formed but its mod-11 check digit is wrong.</summary>
    InvalidChecksum,

    /// <summary>The value is present where this kind of document does not allow it.</summary>
    NotAllowed,

    /// <summary>Amounts do not agree with each other or with the VAT arithmetic.</summary>
    TotalsInconsistent,

    /// <summary>The withheld tax is larger than the document total.</summary>
    WithheldExceedsTotal,

    /// <summary>The text holds a character the embedded fonts cannot print (e.g. CJK), so it would render in a host-dependent fallback font.</summary>
    UnsupportedCharacter,
}
