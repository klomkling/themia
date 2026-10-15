using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

/// <summary>
/// How the tax was borne (เงื่อนไขการหักภาษี). Persisted by name, so renaming a member is a breaking change. What each means in
/// practice is for the accountant (coord #0164 Q11).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WithholdingCondition>))]
public enum WithholdingCondition
{
    /// <summary>หัก ณ ที่จ่าย: withheld from the payment.</summary>
    WithheldAtSource = 1,

    /// <summary>ออกให้ตลอดไป: the payer bears the tax every time.</summary>
    PaidOnBehalfForever = 2,

    /// <summary>ออกให้ครั้งเดียว: the payer bears the tax this once.</summary>
    PaidOnBehalfOnce = 3,

    /// <summary>อื่นๆ: described in <see cref="WithholdingCertificateSnapshot.ConditionOther"/>.</summary>
    Other = 4,
}
