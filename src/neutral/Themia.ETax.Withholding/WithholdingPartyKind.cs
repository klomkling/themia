using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

/// <summary>
/// What a party is, as the form asks it to be written next to the name (ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล). Persisted
/// by name, so renaming a member is a breaking change. Which kinds an income type may be paid to is catalogue data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WithholdingPartyKind>))]
public enum WithholdingPartyKind
{
    /// <summary>บุคคล: a natural person.</summary>
    Individual = 1,

    /// <summary>นิติบุคคล: a juristic person that is none of the more specific kinds below.</summary>
    JuristicPerson = 2,

    /// <summary>บริษัท: a company.</summary>
    Company = 3,

    /// <summary>สมาคม: an association.</summary>
    Association = 4,

    /// <summary>คณะบุคคล: a body of persons (not a juristic person).</summary>
    BodyOfPersons = 5,
}
