using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Themia.Generators.Abstractions.Emission;
using Themia.Generators.Abstractions.Scanning;
using Themia.Generators.Abstractions.Validation;
using Themia.SourceGenerator.Diagnostics;

namespace Themia.SourceGenerator.Generators;

/// <summary>
/// Themia DI source generator. Emits one file:
/// <c>Themia.Generated.ThemiaServiceRegistrations.AddThemiaServices(IServiceCollection)</c>
/// in the consumer's assembly. The method calls every discovered registration
/// (attributes + markers + registrars).
/// </summary>
[Generator]
public sealed class ServiceRegistrationGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline 1: collect all candidate class info for registration + diagnostics.
        var discoveredTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Combine(context.CompilationProvider)
            .SelectMany((pair, _) => CollectTypeInfo(pair.Right, pair.Left));

        var collectedTypes = discoveredTypes.Collect();

        // Pipeline 2: discover IThemiaServiceRegistrar implementations.
        var registrarSyntax = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Combine(context.CompilationProvider)
            .SelectMany((pair, _) => DiscoverRegistrarCandidates(pair.Right, pair.Left));

        var collectedRegistrars = registrarSyntax.Collect();

        var combined = collectedTypes.Combine(collectedRegistrars);
        context.RegisterSourceOutput(combined, (ctx, both) =>
        {
            var (typeInfos, registrars) = both;

            var writer = new ThemiaSourceWriter()
                .WithFileHeader()
                .WithUsings("Microsoft.Extensions.DependencyInjection")
                .WithNamespace("Themia.Generated")
                .OpenClass("ThemiaServiceRegistrations", isStatic: true)
                .OpenMethod("public static IServiceCollection AddThemiaServices(this IServiceCollection services)");

            var registrations = new List<RegistrationRecord>();

            foreach (var info in typeInfos)
            {
                ProcessTypeInfo(ctx, info, registrations);
            }

            // De-dup by (impl, service) pair and sort for deterministic output.
            var sorted = registrations
                .GroupBy(r => (r.ImplementationFullName, r.ServiceFullName))
                .Select(g => g.First())
                .OrderBy(r => r.ImplementationFullName, StringComparer.Ordinal)
                .ToImmutableArray();

            if (sorted.Length > 0)
            {
                writer.AppendLine();
                foreach (var reg in sorted)
                {
                    writer.AppendLine($"services.Add{reg.Lifetime}<{reg.ServiceFullName}, {reg.ImplementationFullName}>();");
                }
            }

            // Process registrar candidates: emit diagnostics, collect valid ones.
            var validRegistrars = new List<string>();
            foreach (var candidate in registrars)
            {
                if (!candidate.HasPublicParameterlessCtor)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RegistrarMissingPublicCtor,
                        candidate.Location,
                        candidate.Name));
                    continue;
                }

                if (candidate.IsInternal)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RegistrarIsInternal,
                        candidate.Location,
                        candidate.Name));
                    // Still emit the registration even though it's internal.
                }

                validRegistrars.Add(candidate.GlobalQualifiedName);
            }

            var sortedRegistrars = validRegistrars
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToImmutableArray();

            if (sortedRegistrars.Length > 0)
            {
                writer.AppendLine();
                foreach (var registrar in sortedRegistrars)
                {
                    writer.AppendLine($"new {registrar}().Register(services);");
                }
            }

            writer
                .AppendLine()
                .AppendLine("return services;")
                .CloseMethod()
                .CloseClass();

            ctx.AddSource("ThemiaServiceRegistrations.g.cs", writer.ToSourceText());
        });
    }

    // -------------------------------------------------------------------------
    // CollectTypeInfo: gathers all info needed for conflict detection + emission
    // -------------------------------------------------------------------------

    private static IEnumerable<DiscoveredTypeInfo> CollectTypeInfo(
        Compilation compilation,
        ClassDeclarationSyntax classDecl)
    {
        var model = compilation.GetSemanticModel(classDecl.SyntaxTree);
        if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type) yield break;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) yield break;

        // Collect all lifetime attributes on the type.
        var lifetimeAttrs = new List<(AttributeData Attr, string Lifetime, bool IsLegacy)>();
        foreach (var attr in type.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null) continue;
            var (lifetime, isLegacy) = ResolveAttributeLifetimeWithLegacyFlag(attrClass);
            if (lifetime is null) continue;
            lifetimeAttrs.Add((attr, lifetime, isLegacy));
        }

        // Collect marker interface lifetimes.
        var markerLifetimes = CollectMarkerLifetimes(type);

        // Collect generic marker service type.
        INamedTypeSymbol? genericMarkerServiceType = null;
        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType || iface.TypeArguments.Length != 1)
                continue;

            var origDef = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (origDef is not ("global::Themia.DependencyInjection.IScopedService<TService>"
                or "global::Themia.DependencyInjection.ISingletonService<TService>"
                or "global::Themia.DependencyInjection.ITransientService<TService>"))
                continue;

            if (iface.TypeArguments[0] is not INamedTypeSymbol named) continue;

            genericMarkerServiceType = named;
            break;
        }

        // Read the ServiceType named arg from the attribute (e.g. [Scoped(ServiceType = typeof(IFoo))]).
        INamedTypeSymbol? attributeServiceType = null;
        switch (lifetimeAttrs.Count)
        {
            case 1:
            {
                var namedArgs = lifetimeAttrs[0].Attr.NamedArguments;
                foreach (var kv in namedArgs)
                {
                    if (kv is { Key: "ServiceType", Value.Value: INamedTypeSymbol svc })
                    {
                        attributeServiceType = svc;
                        break;
                    }
                }

                break;
            }
            // Only yield if there's something to process.
            case 0 when markerLifetimes.Count == 0:
                yield break;
        }

        yield return new DiscoveredTypeInfo(
            type: type,
            classDecl: classDecl,
            lifetimeAttrs: lifetimeAttrs,
            markerLifetimes: markerLifetimes,
            genericMarkerServiceType: genericMarkerServiceType,
            attributeServiceType: attributeServiceType);
    }

    private static List<string> CollectMarkerLifetimes(INamedTypeSymbol type)
    {
        var lifetimes = new List<string>();
        foreach (var iface in type.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            switch (fullName)
            {
                case "global::Themia.DependencyInjection.IScopedService":
                case "global::Themia.DependencyInjection.IScopedService<TService>":
                    if (!lifetimes.Contains("Scoped")) lifetimes.Add("Scoped");
                    break;
                case "global::Themia.DependencyInjection.ISingletonService":
                case "global::Themia.DependencyInjection.ISingletonService<TService>":
                    if (!lifetimes.Contains("Singleton")) lifetimes.Add("Singleton");
                    break;
                case "global::Themia.DependencyInjection.ITransientService":
                case "global::Themia.DependencyInjection.ITransientService<TService>":
                    if (!lifetimes.Contains("Transient")) lifetimes.Add("Transient");
                    break;
            }
        }
        return lifetimes;
    }

    // -------------------------------------------------------------------------
    // ProcessTypeInfo: apply conflict rules, emit diagnostics, build registrations
    // -------------------------------------------------------------------------

    private static void ProcessTypeInfo(
        SourceProductionContext ctx,
        DiscoveredTypeInfo info,
        List<RegistrationRecord> registrations)
    {
        var classLocation = info.ClassDecl.Identifier.GetLocation();
        var typeFullDisplayName = info.Type.ToDisplayString();

        // THEMIA001: Multiple lifetime attributes.
        if (info.LifetimeAttrs.Count > 1)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MultipleLifetimeAttributes,
                classLocation,
                typeFullDisplayName));
            return;
        }

        // THEMIA002: Multiple lifetime markers (distinct lifetimes).
        if (info.MarkerLifetimes.Count > 1)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MultipleLifetimeMarkers,
                classLocation,
                typeFullDisplayName));
            return;
        }

        var attrLifetimeStr = info.LifetimeAttrs.Count == 1 ? info.LifetimeAttrs[0].Lifetime : null;
        var markerLifetimeStr = info.MarkerLifetimes.Count == 1 ? info.MarkerLifetimes[0] : null;

        // Convert string lifetimes to enum for LifetimeResolver.
        var attrLifetime = ParseLifetime(attrLifetimeStr);
        var markerLifetime = ParseLifetime(markerLifetimeStr);

        // THEMIA003 / THEMIA004: Attribute + marker conflict resolution.
        var resolvedLifetimeStr = attrLifetimeStr ?? markerLifetimeStr;
        if (attrLifetime.HasValue && markerLifetime.HasValue)
        {
            var (_, conflict) = LifetimeResolver.Resolve(attrLifetime, markerLifetime);
            switch (conflict)
            {
                case LifetimeConflict.Disagreement:
                    // THEMIA003: Attribute and marker disagree.
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AttributeMarkerDisagreement,
                        classLocation,
                        typeFullDisplayName));
                    return;
                case LifetimeConflict.Redundant:
                    // THEMIA004: Redundant — warn but continue with attribute lifetime.
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RedundantLifetimeAttributeAndMarker,
                        classLocation,
                        typeFullDisplayName));
                    // continue to emit using attrLifetimeStr
                    break;
                case LifetimeConflict.None:
                case LifetimeConflict.NoneSpecified:
                case null:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // THEMIA010: Legacy attribute usage — report (Error as of 0.9.0) but
        // continue emission, so an .editorconfig downgrade still registers the type.
        if (info.LifetimeAttrs.Count == 1)
        {
            switch (info.LifetimeAttrs[0].IsLegacy)
            {
                case true:
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.LegacyAttributeUsage,
                        classLocation,
                        typeFullDisplayName));
                    // continue emission
                    break;
            }
        }

        // THEMIA007: Attribute ServiceType conflicts with generic marker service type.
        if (info.AttributeServiceType is not null && info.GenericMarkerServiceType is not null)
        {
            var attrSvcFull = info.AttributeServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var markerSvcFull = info.GenericMarkerServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (attrSvcFull != markerSvcFull)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AttributeServiceTypeConflictsWithGenericMarker,
                    classLocation,
                    typeFullDisplayName));
                return;
            }
        }

        // Determine final service type.
        INamedTypeSymbol? serviceType;

        if (info.AttributeServiceType is not null)
        {
            serviceType = info.AttributeServiceType;
        }
        else if (info.GenericMarkerServiceType is not null)
        {
            serviceType = info.GenericMarkerServiceType;
        }
        else
        {
            // Fall back to I{ClassName} convention.
            ServiceTypeResolver.TryResolveByConvention(info.Type, out serviceType);
        }

        if (serviceType is null)
        {
            // Determine which specific warning to emit.
            if (attrLifetimeStr is null && markerLifetimeStr is not null)
            {
                // Marker-only path: no service type found.
                // Check if it's ambiguous (multiple non-IXxxService interfaces) vs. just missing.
                var candidateInterfaces = info.Type.AllInterfaces
                    .Where(i => !IsMarkerInterface(i))
                    .ToList();

                if (candidateInterfaces.Count > 1)
                {
                    // THEMIA005: Ambiguous service type.
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AmbiguousServiceType,
                        classLocation,
                        typeFullDisplayName));
                }
                else
                {
                    // THEMIA006: Cannot register — no service interface.
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.CannotRegister,
                        classLocation,
                        typeFullDisplayName,
                        "no matching service interface found and AllowSelfRegistration is false"));
                }
            }
            else if (attrLifetimeStr is not null)
            {
                // Attribute-only path: convention failed.
                // Could be ambiguous or simply missing.
                var candidateInterfaces = info.Type.AllInterfaces
                    .Where(i => !IsMarkerInterface(i))
                    .ToList();

                if (candidateInterfaces.Count > 1)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AmbiguousServiceType,
                        classLocation,
                        typeFullDisplayName));
                }
                // else: convention just didn't match — skip silently (no IFoo interface)
            }
            return;
        }

        registrations.Add(new RegistrationRecord(
            ToGlobalQualified(info.Type),
            ToGlobalQualified(serviceType),
            resolvedLifetimeStr!));
    }

    private static bool IsMarkerInterface(INamedTypeSymbol iface)
    {
        var fullName = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName is
            "global::Themia.DependencyInjection.IScopedService" or
            "global::Themia.DependencyInjection.IScopedService<TService>" or
            "global::Themia.DependencyInjection.ISingletonService" or
            "global::Themia.DependencyInjection.ISingletonService<TService>" or
            "global::Themia.DependencyInjection.ITransientService" or
            "global::Themia.DependencyInjection.ITransientService<TService>" or
            "global::Themia.DependencyInjection.IThemiaServiceRegistrar";
    }

    private static Lifetime? ParseLifetime(string? s) => s switch
    {
        "Scoped" => Lifetime.Scoped,
        "Singleton" => Lifetime.Singleton,
        "Transient" => Lifetime.Transient,
        _ => null
    };

    private static IEnumerable<RegistrarCandidate> DiscoverRegistrarCandidates(
        Compilation compilation,
        ClassDeclarationSyntax classDecl)
    {
        var model = compilation.GetSemanticModel(classDecl.SyntaxTree);
        if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type) yield break;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) yield break;

        if (!type.AllInterfaces.Any(i =>
                i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    == "global::Themia.DependencyInjection.IThemiaServiceRegistrar"))
            yield break;

        var hasParameterlessCtor = type.Constructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        var isInternal = type.DeclaredAccessibility == Accessibility.Internal;
        var location = type.Locations.FirstOrDefault() ?? Location.None;

        yield return new RegistrarCandidate(
            name: type.Name,
            globalQualifiedName: ToGlobalQualified(type),
            hasPublicParameterlessCtor: hasParameterlessCtor,
            isInternal: isInternal,
            location: location);
    }

    private static (string? Lifetime, bool IsLegacy) ResolveAttributeLifetimeWithLegacyFlag(INamedTypeSymbol attrClass)
    {
        var name = attrClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name switch
        {
            "global::Themia.DependencyInjection.ScopedAttribute"    => ("Scoped", false),
            "global::Themia.DependencyInjection.SingletonAttribute" => ("Singleton", false),
            "global::Themia.DependencyInjection.TransientAttribute" => ("Transient", false),
            _ => (null, false)
        };
    }

    // FullyQualifiedFormat already prefixes global:: on the type AND its type arguments.
    // (A "global:: + Replace(\"global::\", \"\")" round-trip would strip qualification off the
    // type arguments of a closed generic — e.g. global::Ns.Repo<Ns.Entity> — and reintroduce
    // ambiguity in the generated code.)
    private static string ToGlobalQualified(INamedTypeSymbol s) =>
        s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // -------------------------------------------------------------------------
    // Data types
    // -------------------------------------------------------------------------

    private sealed class DiscoveredTypeInfo(
        INamedTypeSymbol type,
        ClassDeclarationSyntax classDecl,
        List<(AttributeData Attr, string Lifetime, bool IsLegacy)> lifetimeAttrs,
        List<string> markerLifetimes,
        INamedTypeSymbol? genericMarkerServiceType,
        INamedTypeSymbol? attributeServiceType)
    {
        public INamedTypeSymbol Type { get; } = type;
        public ClassDeclarationSyntax ClassDecl { get; } = classDecl;
        public List<(AttributeData Attr, string Lifetime, bool IsLegacy)> LifetimeAttrs { get; } = lifetimeAttrs;
        public List<string> MarkerLifetimes { get; } = markerLifetimes;
        public INamedTypeSymbol? GenericMarkerServiceType { get; } = genericMarkerServiceType;
        public INamedTypeSymbol? AttributeServiceType { get; } = attributeServiceType;
    }

    private sealed class RegistrarCandidate(
        string name,
        string globalQualifiedName,
        bool hasPublicParameterlessCtor,
        bool isInternal,
        Location location)
    {
        public string Name { get; } = name;
        public string GlobalQualifiedName { get; } = globalQualifiedName;
        public bool HasPublicParameterlessCtor { get; } = hasPublicParameterlessCtor;
        public bool IsInternal { get; } = isInternal;
        public Location Location { get; } = location;
    }

    private sealed class RegistrationRecord(string implementationFullName, string serviceFullName, string lifetime)
    {
        public string ImplementationFullName { get; } = implementationFullName;
        public string ServiceFullName { get; } = serviceFullName;
        public string Lifetime { get; } = lifetime;
    }
}
