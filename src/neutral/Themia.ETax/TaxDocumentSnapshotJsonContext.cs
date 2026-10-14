using System.Text.Json.Serialization;

namespace Themia.ETax;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(TaxDocumentSnapshot))]
internal sealed partial class TaxDocumentSnapshotJsonContext : JsonSerializerContext;
