#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Themia.SourceGenerator.Diagnostics;
using Themia.SourceGenerator.Models;
using Themia.SourceGenerator.Utilities;

namespace Themia.SourceGenerator.Discovery;

/// <summary>
/// Discovers mediator handlers in the compilation.
/// </summary>
internal static class HandlerDiscovery
{
    /// <summary>
    /// Finds all mediator handlers in a class declaration.
    /// </summary>
    public static ImmutableArray<HandlerModel> FindHandlers(
        ClassDeclarationSyntax classSyntax,
        SemanticModel semanticModel,
        Compilation compilation,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        var diagnosticList = new List<Diagnostic>();
        var handlers = new List<HandlerModel>();

        // Get the type symbol for the class
        if (semanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol classSymbol)
        {
            diagnostics = ImmutableArray<Diagnostic>.Empty;
            return ImmutableArray<HandlerModel>.Empty;
        }

        // Skip abstract classes
        if (classSymbol.IsAbstract)
        {
            diagnostics = ImmutableArray<Diagnostic>.Empty;
            return ImmutableArray<HandlerModel>.Empty;
        }

        // Skip pipeline behaviors (they're not handlers)
        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.OriginalDefinition.ToDisplayString() == "Themia.Mediator.Abstractions.IPipelineBehavior<TRequest, TResponse>")
            {
                diagnostics = ImmutableArray<Diagnostic>.Empty;
                return ImmutableArray<HandlerModel>.Empty;
            }
        }

        // Only types that actually implement IRequestHandler<,> are handler candidates.
        // The syntax predicate feeds EVERY class in the assembly here, so the accessibility
        // and closed-generic validations below must run only for real handlers — otherwise
        // THEMIA012/THEMIA013 would fire as errors on a consumer's unrelated generic or
        // inaccessible classes.
        var isHandler = classSymbol.AllInterfaces.Any(iface =>
            iface.OriginalDefinition.ToDisplayString() == "Themia.Mediator.Abstractions.IRequestHandler<TRequest, TResponse>");
        if (!isHandler)
        {
            diagnostics = ImmutableArray<Diagnostic>.Empty;
            return ImmutableArray<HandlerModel>.Empty;
        }

        // Check if accessible for DI
        if (!classSymbol.IsAccessibleForDI())
        {
            diagnosticList.Add(Diagnostic.Create(
                DiagnosticDescriptors.HandlerNotAccessible,
                classSyntax.GetLocation(),
                classSymbol.Name));

            diagnostics = diagnosticList.ToImmutableArray();
            return ImmutableArray<HandlerModel>.Empty;
        }

        // Check if it's a closed generic type
        if (!classSymbol.IsClosedGenericType())
        {
            diagnosticList.Add(Diagnostic.Create(
                DiagnosticDescriptors.OpenGenericHandler,
                classSyntax.GetLocation(),
                classSymbol.Name));

            diagnostics = diagnosticList.ToImmutableArray();
            return ImmutableArray<HandlerModel>.Empty;
        }

        // Find all handler interfaces implemented
        foreach (var iface in classSymbol.AllInterfaces)
        {
            var handlerModel = TryCreateHandlerModel(classSymbol, iface, classSyntax.GetLocation());
            if (handlerModel != null)
            {
                handlers.Add(handlerModel);
            }
        }

        diagnostics = diagnosticList.ToImmutableArray();
        return handlers.ToImmutableArray();
    }

    private static HandlerModel? TryCreateHandlerModel(
        INamedTypeSymbol handlerType,
        INamedTypeSymbol interfaceType,
        Location location)
    {
        // Check if this is IRequestHandler<,>
        if (interfaceType.OriginalDefinition.ToDisplayString() == "Themia.Mediator.Abstractions.IRequestHandler<TRequest, TResponse>")
        {
            if (interfaceType.TypeArguments.Length == 2 &&
                interfaceType.TypeArguments[0] is INamedTypeSymbol requestType &&
                interfaceType.TypeArguments[1] is ITypeSymbol responseType)
            {
                var kind = DetermineHandlerKind(requestType);
                var lifetime = GetLifetime(handlerType);
                return new HandlerModel(handlerType, kind, requestType, responseType, lifetime);
            }
        }

        return null;
    }

    private static HandlerKind DetermineHandlerKind(INamedTypeSymbol requestType)
    {
        // Check if it implements ICommand<>
        foreach (var iface in requestType.AllInterfaces)
        {
            if (iface.OriginalDefinition.ToDisplayString() == "Themia.Mediator.Abstractions.ICommand<TResponse>")
            {
                return HandlerKind.Command;
            }

            if (iface.OriginalDefinition.ToDisplayString() == "Themia.Mediator.Abstractions.IQuery<TResponse>")
            {
                return HandlerKind.Query;
            }
        }

        return HandlerKind.Request;
    }

    private static ServiceLifetime GetLifetime(INamedTypeSymbol handlerType)
    {
        // Look for lifetime attributes from Themia.DependencyInjection
        foreach (var attr in handlerType.GetAttributes())
        {
            var attributeClass = attr.AttributeClass;
            if (attributeClass == null) continue;

            var fullName = attributeClass.ToDisplayString();
            var shortName = attributeClass.Name;

            // Check for lifetime attributes (in Themia.DependencyInjection namespace)
            if (fullName == "Themia.DependencyInjection.SingletonAttribute" ||
                shortName == "SingletonAttribute")
            {
                return ServiceLifetime.Singleton;
            }
            else if (fullName == "Themia.DependencyInjection.TransientAttribute" ||
                     shortName == "TransientAttribute")
            {
                return ServiceLifetime.Transient;
            }
            else if (fullName == "Themia.DependencyInjection.ScopedAttribute" ||
                     shortName == "ScopedAttribute")
            {
                return ServiceLifetime.Scoped;
            }
        }

        // Default to Scoped
        return ServiceLifetime.Scoped;
    }
}
