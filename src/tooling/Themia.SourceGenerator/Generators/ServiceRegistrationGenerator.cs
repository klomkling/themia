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
    // Fully-qualified metadata names for the three DI lifetime attributes.
    private const string ScopedAttributeFqn     = "Themia.DependencyInjection.ScopedAttribute";
    private const string SingletonAttributeFqn  = "Themia.DependencyInjection.SingletonAttribute";
    private const string TransientAttributeFqn  = "Themia.DependencyInjection.TransientAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline 1a: attribute-bearing classes — discovered via ForAttributeWithMetadataName,
        // which filters by attribute FQN up-front and caches per-class; no CompilationProvider needed.
        // Each pipeline covers one attribute; we merge all three into a single stream.
        var scopedAttrs    = context.SyntaxProvider
            .ForAttributeWithMetadataName(ScopedAttributeFqn,
                predicate:  (node, _) => node is ClassDeclarationSyntax,
                transform:  (ctx, _)  => CollectTypeInfoFromAttributeContext(ctx))
            .SelectMany((items, _) => items);

        var singletonAttrs = context.SyntaxProvider
            .ForAttributeWithMetadataName(SingletonAttributeFqn,
                predicate:  (node, _) => node is ClassDeclarationSyntax,
                transform:  (ctx, _)  => CollectTypeInfoFromAttributeContext(ctx))
            .SelectMany((items, _) => items);

        var transientAttrs = context.SyntaxProvider
            .ForAttributeWithMetadataName(TransientAttributeFqn,
                predicate:  (node, _) => node is ClassDeclarationSyntax,
                transform:  (ctx, _)  => CollectTypeInfoFromAttributeContext(ctx))
            .SelectMany((items, _) => items);

        // Pipeline 1b: marker-interface-only classes (no DI attribute).
        // Narrowed predicate: only classes that declare a base list (implements/inherits something).
        // Uses the per-node SemanticModel — no CompilationProvider needed.
        var markerOnly = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate:  (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform:  (ctx, _)  => CollectMarkerOnlyTypeInfo(ctx))
            .SelectMany((items, _) => items);

        // Merge attribute + marker-only pipelines into one collected provider.
        // Chain: scoped ++ singleton ++ transient ++ markerOnly, all flattened.
        var collectedTypes = scopedAttrs
            .Collect()
            .Combine(singletonAttrs.Collect())
            .Select((pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(transientAttrs.Collect())
            .Select((pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(markerOnly.Collect())
            .Select((pair, _) => pair.Left.AddRange(pair.Right));

        // Pipeline 2: discover IThemiaServiceRegistrar implementations.
        // Narrowed to classes with a base list; uses per-node SemanticModel.
        var registrarSyntax = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate:  (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform:  (ctx, _)  => DiscoverRegistrarCandidatesFromContext(ctx))
            .SelectMany((items, _) => items);

        var collectedRegistrars = registrarSyntax.Collect();

        var combined = collectedTypes.Combine(collectedRegistrars);
        context.RegisterSourceOutput(combined, (ctx, both) =>
        {
            var (typeInfos, registrars) = both;

            var writer = new ThemiaSourceWriter()
                .WithFileHeader()
                .WithUsings("Microsoft.Extensions.DependencyInjection")
                .WithNamespace("Themia.Generated")
                .OpenClass("ThemiaServiceRegistrations", isStatic: true, isInternal: true)
                .OpenMethod("public static IServiceCollection AddThemiaServices(this IServiceCollection services)");

            var registrations = new List<RegistrationRecord>();

            foreach (var info in typeInfos)
            {
                ProcessTypeInfo(ctx, info, registrations);
            }

            // De-dup by (impl, service, key) and sort for deterministic output.
            var sorted = registrations
                .GroupBy(r => (r.ImplementationFullName, r.ServiceFullName, r.Key))
                .Select(g => g.First())
                .OrderBy(r => r.ImplementationFullName, StringComparer.Ordinal)
                .ThenBy(r => r.ServiceFullName, StringComparer.Ordinal)
                .ThenBy(r => r.Key ?? string.Empty, StringComparer.Ordinal)
                .ToImmutableArray();

            if (sorted.Length > 0)
            {
                writer.AppendLine();
                foreach (var reg in sorted)
                {
                    // ServiceKey set → keyed registration (AddKeyedScoped/Singleton/Transient).
                    writer.AppendLine(reg.Key is null
                        ? $"services.Add{reg.Lifetime}<{reg.ServiceFullName}, {reg.ImplementationFullName}>();"
                        : $"services.AddKeyed{reg.Lifetime}<{reg.ServiceFullName}, {reg.ImplementationFullName}>({Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(reg.Key, quote: true)});");
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
    // CollectTypeInfoFromAttributeContext: attribute path (ForAttributeWithMetadataName)
    // TargetSymbol is already resolved — no CompilationProvider needed.
    // -------------------------------------------------------------------------

    private static ImmutableArray<DiscoveredTypeInfo> CollectTypeInfoFromAttributeContext(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return ImmutableArray<DiscoveredTypeInfo>.Empty;
        if (ctx.TargetNode is not ClassDeclarationSyntax classDecl) return ImmutableArray<DiscoveredTypeInfo>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) return ImmutableArray<DiscoveredTypeInfo>.Empty;

        return CollectTypeInfoCore(type, classDecl);
    }

    // -------------------------------------------------------------------------
    // CollectMarkerOnlyTypeInfo: marker-interface path (CreateSyntaxProvider).
    // Skips classes that already have a DI attribute (handled by the attribute pipeline).
    // Uses per-node SemanticModel — no CompilationProvider needed.
    // -------------------------------------------------------------------------

    private static ImmutableArray<DiscoveredTypeInfo> CollectMarkerOnlyTypeInfo(
        GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl) return ImmutableArray<DiscoveredTypeInfo>.Empty;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type)
            return ImmutableArray<DiscoveredTypeInfo>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) return ImmutableArray<DiscoveredTypeInfo>.Empty;

        // Skip if already handled by the attribute pipeline.
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass is null) continue;
            var (lifetime, _) = ResolveAttributeLifetimeWithLegacyFlag(attr.AttributeClass);
            if (lifetime is not null) return ImmutableArray<DiscoveredTypeInfo>.Empty;
        }

        // Only emit if there is at least one marker lifetime.
        var markerLifetimes = CollectMarkerLifetimes(type);
        if (markerLifetimes.Count == 0) return ImmutableArray<DiscoveredTypeInfo>.Empty;

        return CollectTypeInfoCore(type, classDecl);
    }

    // -------------------------------------------------------------------------
    // CollectTypeInfoCore: shared logic for both attribute and marker paths.
    // -------------------------------------------------------------------------

    private static ImmutableArray<DiscoveredTypeInfo> CollectTypeInfoCore(
        INamedTypeSymbol type,
        ClassDeclarationSyntax classDecl)
    {
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

        // Guard: nothing to emit.
        if (lifetimeAttrs.Count == 0 && markerLifetimes.Count == 0)
            return ImmutableArray<DiscoveredTypeInfo>.Empty;

        // Collect generic marker service type(s). A class may implement multiple generic markers
        // with different TService arguments — detect that as ambiguous rather than silently
        // picking the first.
        INamedTypeSymbol? genericMarkerServiceType = null;
        var multipleGenericMarkerServiceTypes = false;
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

            if (genericMarkerServiceType is null)
                genericMarkerServiceType = named;
            else if (!SymbolEqualityComparer.Default.Equals(genericMarkerServiceType, named))
                multipleGenericMarkerServiceTypes = true;
        }

        // Read the ServiceType named arg from the attribute (e.g. [Scoped(ServiceType = typeof(IFoo))]).
        INamedTypeSymbol? attributeServiceType = null;
        var allowSelfRegistration = false;
        string? serviceKey = null;
        if (lifetimeAttrs.Count == 1)
        {
            foreach (var kv in lifetimeAttrs[0].Attr.NamedArguments)
            {
                if (kv is { Key: "ServiceType", Value.Value: INamedTypeSymbol svc })
                    attributeServiceType = svc;
                else if (kv is { Key: "AllowSelfRegistration", Value.Value: bool allow })
                    allowSelfRegistration = allow;
                else if (kv is { Key: "ServiceKey", Value.Value: string key })
                    serviceKey = key;
            }
        }

        return ImmutableArray.Create(new DiscoveredTypeInfo(
            type: type,
            classDecl: classDecl,
            lifetimeAttrs: lifetimeAttrs,
            markerLifetimes: markerLifetimes,
            genericMarkerServiceType: genericMarkerServiceType,
            multipleGenericMarkerServiceTypes: multipleGenericMarkerServiceTypes,
            attributeServiceType: attributeServiceType,
            allowSelfRegistration: allowSelfRegistration,
            serviceKey: serviceKey));
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

        // THEMIA005: Multiple generic markers with different TService — the service type is
        // ambiguous, so don't silently pick one.
        if (info.MultipleGenericMarkerServiceTypes)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AmbiguousServiceType,
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

        // THEMIA010: Legacy attribute usage — report (Error severity) but continue
        // emission, so an .editorconfig downgrade still registers the type.
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
            // Fall back to the I{ClassName} convention, then self-registration if the
            // attribute opted in via AllowSelfRegistration.
            ServiceTypeResolver.TryResolveWithSelfRegistration(
                info.Type, info.AllowSelfRegistration, out serviceType);
        }

        if (serviceType is null)
        {
            // Determine which specific warning to emit.
            if (attrLifetimeStr is null && markerLifetimeStr is not null)
            {
                // Marker-only path: no service type found.
                // Check if it's ambiguous (multiple non-IXxxService interfaces) vs. just missing.
                // Direct interfaces only, to align with TryResolveByConvention (which matches the
                // I{ClassName} convention against Type.Interfaces) — counting transitive interfaces
                // would falsely flag THEMIA005 for a single service interface that extends others.
                var candidateInterfaces = info.Type.Interfaces
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
                // Direct interfaces only, to align with TryResolveByConvention (which matches the
                // I{ClassName} convention against Type.Interfaces) — counting transitive interfaces
                // would falsely flag THEMIA005 for a single service interface that extends others.
                var candidateInterfaces = info.Type.Interfaces
                    .Where(i => !IsMarkerInterface(i))
                    .ToList();

                if (candidateInterfaces.Count > 1)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AmbiguousServiceType,
                        classLocation,
                        typeFullDisplayName));
                }
                else
                {
                    // No I{ClassName} interface and AllowSelfRegistration is false — the same end
                    // state the marker-only path above diagnoses, so emit THEMIA006 instead of
                    // skipping silently (an explicit [Scoped] otherwise vanishes with no feedback).
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.CannotRegister,
                        classLocation,
                        typeFullDisplayName,
                        "no matching service interface found and AllowSelfRegistration is false"));
                }
            }
            return;
        }

        registrations.Add(new RegistrationRecord(
            ToGlobalQualified(info.Type),
            ToGlobalQualified(serviceType),
            resolvedLifetimeStr!,
            info.ServiceKey));
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

    // -------------------------------------------------------------------------
    // DiscoverRegistrarCandidatesFromContext: registrar path (CreateSyntaxProvider).
    // Uses per-node SemanticModel — no CompilationProvider needed.
    // -------------------------------------------------------------------------

    private static ImmutableArray<RegistrarCandidate> DiscoverRegistrarCandidatesFromContext(
        GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl)
            return ImmutableArray<RegistrarCandidate>.Empty;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type)
            return ImmutableArray<RegistrarCandidate>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class)
            return ImmutableArray<RegistrarCandidate>.Empty;

        var isRegistrar = false;
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    == "global::Themia.DependencyInjection.IThemiaServiceRegistrar")
            {
                isRegistrar = true;
                break;
            }
        }

        if (!isRegistrar) return ImmutableArray<RegistrarCandidate>.Empty;

        var hasParameterlessCtor = false;
        foreach (var ctor in type.Constructors)
        {
            if (ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility == Accessibility.Public)
            {
                hasParameterlessCtor = true;
                break;
            }
        }

        var isInternal = type.DeclaredAccessibility == Accessibility.Internal;
        var location = type.Locations.FirstOrDefault() ?? Location.None;

        return ImmutableArray.Create(new RegistrarCandidate(
            name: type.Name,
            globalQualifiedName: ToGlobalQualified(type),
            hasPublicParameterlessCtor: hasParameterlessCtor,
            isInternal: isInternal,
            location: location));
    }

    // IsLegacy / THEMIA010 are reserved scaffolding: Themia is a clean brand with no superseded
    // attribute set, so every current attribute maps to IsLegacy=false and THEMIA010 never fires
    // today. The flag exists so a future "you're using a deprecated attribute, migrate" diagnostic
    // (e.g. detecting leftover Idevs.ComponentModels.* during an Idevs→Themia migration) can be
    // added by mapping those FQNs here with IsLegacy=true — intentionally NOT wired up in 0.2.0.
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
        bool multipleGenericMarkerServiceTypes,
        INamedTypeSymbol? attributeServiceType,
        bool allowSelfRegistration,
        string? serviceKey)
    {
        public INamedTypeSymbol Type { get; } = type;
        public ClassDeclarationSyntax ClassDecl { get; } = classDecl;
        public List<(AttributeData Attr, string Lifetime, bool IsLegacy)> LifetimeAttrs { get; } = lifetimeAttrs;
        public List<string> MarkerLifetimes { get; } = markerLifetimes;
        public bool MultipleGenericMarkerServiceTypes { get; } = multipleGenericMarkerServiceTypes;
        public INamedTypeSymbol? GenericMarkerServiceType { get; } = genericMarkerServiceType;
        public INamedTypeSymbol? AttributeServiceType { get; } = attributeServiceType;
        public bool AllowSelfRegistration { get; } = allowSelfRegistration;
        public string? ServiceKey { get; } = serviceKey;
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

    private sealed class RegistrationRecord(string implementationFullName, string serviceFullName, string lifetime, string? key)
    {
        public string ImplementationFullName { get; } = implementationFullName;
        public string ServiceFullName { get; } = serviceFullName;
        public string Lifetime { get; } = lifetime;
        public string? Key { get; } = key;
    }
}
