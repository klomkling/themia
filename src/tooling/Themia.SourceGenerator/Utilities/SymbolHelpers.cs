#nullable enable
using Microsoft.CodeAnalysis;

namespace Themia.SourceGenerator.Utilities;

/// <summary>
/// Symbol display formats for mediator code generation.
/// </summary>
internal static class SymbolFormats
{
    /// <summary>
    /// Fully qualified format for types (includes global:: prefix).
    /// </summary>
    public static readonly SymbolDisplayFormat FullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                             SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
}

/// <summary>
/// Extension methods for ITypeSymbol used by the mediator handler generator.
/// </summary>
internal static class SymbolExtensions
{
    /// <summary>
    /// Checks if the type is a closed constructed generic type (no unbound type parameters).
    /// </summary>
    public static bool IsClosedGenericType(this INamedTypeSymbol type)
    {
        if (!type.IsGenericType)
            return true;

        foreach (var typeArg in type.TypeArguments)
        {
            if (typeArg.TypeKind == TypeKind.TypeParameter)
                return false;

            if (typeArg is INamedTypeSymbol namedTypeArg && !IsClosedGenericType(namedTypeArg))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the type is accessible for dependency injection (public or internal, not private nested).
    /// </summary>
    public static bool IsAccessibleForDI(this INamedTypeSymbol type)
    {
        // Must be public or internal
        if (type.DeclaredAccessibility != Accessibility.Public &&
            type.DeclaredAccessibility != Accessibility.Internal)
            return false;

        // Check containing types (for nested classes)
        var containingType = type.ContainingType;
        while (containingType != null)
        {
            if (containingType.DeclaredAccessibility != Accessibility.Public &&
                containingType.DeclaredAccessibility != Accessibility.Internal)
                return false;

            containingType = containingType.ContainingType;
        }

        return true;
    }

    /// <summary>
    /// Gets the fully qualified name with global:: prefix.
    /// </summary>
    public static string GetFullyQualifiedName(this ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolFormats.FullyQualified);
    }
}
