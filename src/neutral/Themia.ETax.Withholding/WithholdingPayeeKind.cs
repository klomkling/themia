using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

/// <summary>Who was paid. Persisted by name, so renaming a member is a breaking change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WithholdingPayeeKind>))]
public enum WithholdingPayeeKind
{
    /// <summary>A natural person.</summary>
    Individual = 1,

    /// <summary>A juristic person (company, partnership, foundation ...).</summary>
    Juristic = 2,
}
