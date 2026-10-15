namespace Themia.ETax.Withholding;

/// <summary>Why a field failed validation. Codes only: the values are names and tax ids and are never echoed.</summary>
public enum WithholdingValidationCode
{
    /// <summary>A required value is missing or blank.</summary>
    Required,

    /// <summary>The value has the wrong shape (digits, length), is out of range, or is dated impossibly.</summary>
    Malformed,

    /// <summary>The tax id is well formed but its mod-11 check digit is wrong.</summary>
    InvalidChecksum,

    /// <summary>The value is present where it is not allowed, or the catalogue does not allow it for this income type.</summary>
    NotAllowed,

    /// <summary>The certificate disagrees with the income-type catalogue it names (unknown code or version, or a printed copy that differs).</summary>
    CatalogueMismatch,

    /// <summary>Amounts do not agree with each other, with the rate, or with the totals.</summary>
    AmountsInconsistent,

    /// <summary>The tax withheld is larger than the amount paid.</summary>
    WithheldExceedsPaid,

    /// <summary>The text holds a character the embedded fonts cannot print (e.g. CJK), so it would render in a host-dependent fallback font.</summary>
    UnsupportedCharacter,
}
