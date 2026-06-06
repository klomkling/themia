namespace Themia.SourceGenerator.Generators;

/// <summary>
/// Fully-resolved, equatable output of analyzing one candidate type. Holds only strings/value types —
/// no Roslyn symbols/syntax — so the incremental pipeline can cache it and the output stage emits
/// source without touching the compilation.
/// </summary>
/// <remarks>
/// <paramref name="TypeFullName"/> is the global::-qualified implementation type name. It is always
/// populated (even for diagnostic-only results) because the output stage dedups the merged type
/// stream by it: a class carrying more than one lifetime attribute enters multiple
/// <c>ForAttributeWithMetadataName</c> pipelines and would otherwise be processed (and re-diagnosed)
/// once per attribute.
/// </remarks>
internal sealed record DiscoveredRegistration(
    string TypeFullName,                  // global::-qualified implementation type; also the dedup key
    RegistrationData? Registration,       // present XOR diagnostics-only — the correlated registration fields
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool HasRegistration => Registration is not null;
}

/// <summary>
/// The correlated payload of a resolved registration. Bundled into one optional value on
/// <see cref="DiscoveredRegistration"/> so a half-populated state (e.g. a service type without a
/// lifetime) is unrepresentable: a registration is either fully present or absent.
/// </summary>
internal sealed record RegistrationData(
    string ServiceFullName,           // global::-qualified service type
    string Lifetime,                  // "Scoped" | "Singleton" | "Transient"
    string? ServiceKey);

/// <summary>
/// Equatable, compilation-free description of an <c>IThemiaServiceRegistrar</c> candidate. Carries
/// only strings/bools plus pre-captured diagnostics so the registrar pipeline caches and the output
/// stage emits without touching the compilation.
/// </summary>
internal sealed record RegistrarInfo(
    string GlobalQualifiedName,
    bool HasPublicParameterlessCtor,
    bool IsInternal,
    EquatableArray<DiagnosticInfo> Diagnostics);
