using Microsoft.CodeAnalysis;

namespace Themia.Generators.Abstractions.Scanning;

/// <summary>
/// Compilation-wide search helpers for finding types by attribute, by
/// interface implementation, or by attribute-implementing-interface.
/// All methods skip abstract classes and interfaces — only concrete types
/// are returned, which is the typical DI-registration target.
/// </summary>
public static class ThemiaRoslynScanner
{
    /// <summary>Returns all concrete types in <paramref name="compilation"/> that carry the named attribute.</summary>
    public static IEnumerable<INamedTypeSymbol> FindByAttribute(Compilation compilation, string fullAttributeName)
    {
        if (string.IsNullOrEmpty(fullAttributeName)) throw new ArgumentException("fullAttributeName cannot be empty", nameof(fullAttributeName));

        return EnumerateConcreteTypes(compilation)
            .Where(t => t.HasAttributeWithFullName(fullAttributeName));
    }

    /// <summary>Returns all concrete types in <paramref name="compilation"/> that implement any of the named interfaces.</summary>
    public static IEnumerable<INamedTypeSymbol> FindByInterface(Compilation compilation, params string[] fullInterfaceNames)
    {
        if (fullInterfaceNames is null || fullInterfaceNames.Length == 0)
            throw new ArgumentException("At least one interface name is required.", nameof(fullInterfaceNames));

        return EnumerateConcreteTypes(compilation)
            .Where(t => fullInterfaceNames.Any(t.ImplementsInterface));
    }

    /// <summary>
    /// Returns all concrete types in <paramref name="compilation"/> that carry an attribute
    /// whose attribute class itself implements <paramref name="fullInterfaceName"/>.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> FindByAttributeImplementingInterface(
        Compilation compilation, string fullInterfaceName)
    {
        if (string.IsNullOrEmpty(fullInterfaceName)) throw new ArgumentException("fullInterfaceName cannot be empty", nameof(fullInterfaceName));

        return EnumerateConcreteTypes(compilation)
            .Where(t => t.GetAttributes().Any(a =>
                a.AttributeClass is { } attrClass &&
                attrClass.ImplementsInterface(fullInterfaceName)));
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateConcreteTypes(Compilation compilation)
    {
        return EnumerateAllTypes(compilation.GlobalNamespace)
            .Where(t => t.TypeKind == TypeKind.Class && !t.IsAbstract);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                foreach (var t in EnumerateAllTypes(ns)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateNested(type)) yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested)) yield return deeper;
        }
    }
}
