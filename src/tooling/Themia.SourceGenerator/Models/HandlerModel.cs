#nullable enable
using Microsoft.CodeAnalysis;

namespace Themia.SourceGenerator.Models;

/// <summary>
/// Represents the kind of handler interface implemented.
/// </summary>
internal enum HandlerKind
{
    Request,
    Command,
    Query
}

/// <summary>
/// Represents the DI service lifetime for a handler/service.
/// Values match Microsoft.Extensions.DependencyInjection.ServiceLifetime.
/// </summary>
internal enum ServiceLifetime
{
    Singleton = 0,
    Scoped = 1,
    Transient = 2
}

/// <summary>
/// Model representing a discovered mediator handler for code generation.
/// </summary>
internal sealed class HandlerModel
{
    public INamedTypeSymbol HandlerType { get; }
    public HandlerKind Kind { get; }
    public INamedTypeSymbol RequestType { get; }
    public ITypeSymbol ResponseType { get; }
    public ServiceLifetime Lifetime { get; }
    public string SafeHintName { get; }

    public HandlerModel(
        INamedTypeSymbol handlerType,
        HandlerKind kind,
        INamedTypeSymbol requestType,
        ITypeSymbol responseType,
        ServiceLifetime lifetime)
    {
        HandlerType = handlerType;
        Kind = kind;
        RequestType = requestType;
        ResponseType = responseType;
        Lifetime = lifetime;

        // Generate a safe hint name for file naming
        SafeHintName = GenerateSafeHintName(handlerType, requestType);
    }

    private static string GenerateSafeHintName(INamedTypeSymbol handlerType, INamedTypeSymbol requestType)
    {
        // Readable prefix (simple names, generic arity markers stripped)...
        var handlerName = handlerType.Name.Replace("`", "_");
        var requestName = requestType.Name.Replace("`", "_");

        // ...plus a deterministic disambiguator from the fully-qualified names, so handlers that
        // share simple names across namespaces don't collide on the generated hint/method names
        // (which would fail AddSource / produce duplicate Add_*/Invoke_* members).
        var disambiguator = StableHash($"{handlerType.ToDisplayString()}|{requestType.ToDisplayString()}");

        return $"{handlerName}_{requestName}_{disambiguator}";
    }

    private static string StableHash(string value)
    {
        // FNV-1a (32-bit) — deterministic across processes, unlike string.GetHashCode.
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash.ToString("x8");
    }

    public override bool Equals(object? obj)
    {
        if (obj is not HandlerModel other)
            return false;

        return SymbolEqualityComparer.Default.Equals(HandlerType, other.HandlerType)
            && SymbolEqualityComparer.Default.Equals(RequestType, other.RequestType)
            && SymbolEqualityComparer.Default.Equals(ResponseType, other.ResponseType)
            && Kind == other.Kind;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = SymbolEqualityComparer.Default.GetHashCode(HandlerType);
            hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(RequestType);
            hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(ResponseType);
            hash = (hash * 397) ^ (int)Kind;
            return hash;
        }
    }
}
