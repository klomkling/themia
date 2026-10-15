using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(WithholdingCertificateSnapshot))]
internal sealed partial class WithholdingSnapshotJsonContext : JsonSerializerContext;
