using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

/// <summary>
/// The monthly return a certificate belongs to: the form boxes of the certificate (ลำดับที่ ... ในแบบ). Persisted by name, so
/// renaming a member is a breaking change. The seven members are the seven forms the official certificate lists, in its order and numbering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WithholdingFilingForm>))]
public enum WithholdingFilingForm
{
    /// <summary>ภ.ง.ด.1ก.</summary>
    Pnd1A = 1,

    /// <summary>ภ.ง.ด.1ก พิเศษ.</summary>
    Pnd1ASpecial = 2,

    /// <summary>ภ.ง.ด.2.</summary>
    Pnd2 = 3,

    /// <summary>ภ.ง.ด.3.</summary>
    Pnd3 = 4,

    /// <summary>ภ.ง.ด.2ก.</summary>
    Pnd2A = 5,

    /// <summary>ภ.ง.ด.3ก.</summary>
    Pnd3A = 6,

    /// <summary>ภ.ง.ด.53.</summary>
    Pnd53 = 7,
}
