#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Themia.SourceGenerator.Diagnostics;
using Themia.SourceGenerator.Discovery;
using Themia.SourceGenerator.Models;
using Themia.SourceGenerator.Utilities;

namespace Themia.SourceGenerator.Generators;

/// <summary>
/// Incremental source generator for Themia Mediator handlers.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MediatorHandlerGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the incremental source generator by registering syntax/compilation
    /// transforms and source outputs.
    /// </summary>
    /// <param name="context">The initialization context provided by the compiler.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Detect if assembly has [GenerateMediatorHandlers] attribute
        var assemblyHasOptIn = context.CompilationProvider
            .Select((compilation, _) =>
            {
                foreach (var attr in compilation.Assembly.GetAttributes())
                {
                    var attributeClass = attr.AttributeClass;
                    if (attributeClass != null)
                    {
                        var fullName = attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var simpleName = attributeClass.ToDisplayString();

                        // Check both fully qualified and simple name
                        if (fullName == "global::Themia.Mediator.GenerateMediatorHandlersAttribute" ||
                            simpleName == "Themia.Mediator.GenerateMediatorHandlersAttribute" ||
                            attributeClass.Name == "GenerateMediatorHandlersAttribute")
                        {
                            return true;
                        }
                    }
                }
                return false;
            });

        // Find all class declarations
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (ClassSyntax: (ClassDeclarationSyntax)ctx.Node, SemanticModel: ctx.SemanticModel))
            .Where(static x => x.ClassSyntax is not null);

        // Combine with compilation to discover handlers
        var handlersWithDiagnostics = classDeclarations
            .Combine(context.CompilationProvider)
            .Select((tuple, _) =>
            {
                var ((classSyntax, semanticModel), compilation) = tuple;
                var handlers = HandlerDiscovery.FindHandlers(classSyntax, semanticModel, compilation, out var diagnostics);
                return (Handlers: handlers, Diagnostics: diagnostics);
            });

        // Filter to only include when opt-in is present
        var filteredHandlers = handlersWithDiagnostics
            .Combine(assemblyHasOptIn)
            .Where(x => x.Right) // Only if assembly has attribute
            .Select((x, _) => x.Left);

        // Report diagnostics
        context.RegisterSourceOutput(filteredHandlers, (spc, handlersAndDiagnostics) =>
        {
            foreach (var diagnostic in handlersAndDiagnostics.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }
        });

        // Collect all handlers and detect duplicates
        var aggregatedHandlers = filteredHandlers
            .SelectMany((x, _) => x.Handlers)
            .Collect()
            .Select((handlers, _) =>
            {
                var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                var distinct = new Dictionary<string, HandlerModel>();

                var grouped = handlers.GroupBy(handler =>
                    $"{handler.Kind}_{handler.RequestType.GetFullyQualifiedName()}_{handler.ResponseType.GetFullyQualifiedName()}");

                foreach (var group in grouped)
                {
                    var first = group.First();
                    distinct[group.Key] = first;

                    foreach (var duplicate in group.Skip(1))
                    {
                        var location = duplicate.HandlerType.Locations.FirstOrDefault() ?? Location.None;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.DuplicateHandler,
                            location,
                            duplicate.HandlerType.ToDisplayString(SymbolFormats.FullyQualified),
                            first.HandlerType.ToDisplayString(SymbolFormats.FullyQualified),
                            first.RequestType.GetFullyQualifiedName(),
                            first.ResponseType.GetFullyQualifiedName()));
                    }
                }

                return (Handlers: distinct.Values.ToImmutableArray(), Diagnostics: diagnostics.ToImmutableArray());
            });

        context.RegisterSourceOutput(aggregatedHandlers, (spc, data) =>
        {
            foreach (var diagnostic in data.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }
        });

        var allHandlers = aggregatedHandlers.Select((data, _) => data.Handlers);

        // Generate per-handler files
        var perHandlerSource = allHandlers
            .SelectMany((handlers, _) => handlers.Select(h => h));

        context.RegisterSourceOutput(perHandlerSource, (spc, handler) =>
        {
            var source = SourceBuilder.BuildPerHandler(handler);
            spc.AddSource($"ThemiaMediator.Handler.{handler.SafeHintName}.g.cs", source);
        });

        // Generate registration/dispatcher only when assembly opts in (even if zero handlers)
        var handlersWithOptIn = allHandlers.Combine(assemblyHasOptIn);

        context.RegisterSourceOutput(handlersWithOptIn, (spc, data) =>
        {
            var handlers = data.Left;
            var hasOptIn = data.Right;
            if (!hasOptIn)
                return;

            var source = SourceBuilder.BuildRegistration(handlers);
            spc.AddSource("ThemiaMediator.Registration.g.cs", source);
        });

        context.RegisterSourceOutput(handlersWithOptIn, (spc, data) =>
        {
            var handlers = data.Left;
            var hasOptIn = data.Right;
            if (!hasOptIn)
                return;

            var source = SourceBuilder.BuildDispatcher(handlers);
            spc.AddSource("ThemiaMediator.Dispatcher.g.cs", source);
        });
    }
}
