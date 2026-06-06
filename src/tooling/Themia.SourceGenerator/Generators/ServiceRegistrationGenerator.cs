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
/// <remarks>
/// All semantic analysis runs INSIDE the transforms, which return only equatable, compilation-free
/// data (<see cref="DiscoveredRegistration"/> / <see cref="RegistrarInfo"/> — strings, enums, value
/// types, and <see cref="DiagnosticInfo"/>). No Roslyn symbol, syntax node, or <see cref="Location"/>
/// crosses <c>.Collect()</c>/<c>.Combine()</c> into the output stage, so the per-node cache holds and
/// the compilation is not rooted. The output callback is replay-only: it reports pre-computed
/// diagnostics and emits source from strings.
/// </remarks>
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
            .Select((pair, _) => pair.Left.AddRange(pair.Right))
            .WithTrackingName("Registrations");

        // Pipeline 2: discover IThemiaServiceRegistrar implementations.
        // Narrowed to classes with a base list; uses per-node SemanticModel.
        var registrarSyntax = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate:  (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform:  (ctx, _)  => DiscoverRegistrarCandidatesFromContext(ctx))
            .SelectMany((items, _) => items);

        var collectedRegistrars = registrarSyntax.Collect().WithTrackingName("Registrars");

        var combined = collectedTypes.Combine(collectedRegistrars);
        context.RegisterSourceOutput(combined, static (ctx, both) =>
        {
            var (typeInfos, registrars) = both;

            var writer = new ThemiaSourceWriter()
                .WithFileHeader()
                .WithUsings("Microsoft.Extensions.DependencyInjection")
                .WithNamespace("Themia.Generated")
                .OpenClass("ThemiaServiceRegistrations", isStatic: true, isInternal: true)
                .OpenMethod("public static IServiceCollection AddThemiaServices(this IServiceCollection services)");

            var registrations = new List<RegistrationRecord>();

            // Dedup by fully-qualified type name before replaying diagnostics + registrations.
            // A class annotated with more than one lifetime attribute (e.g. [Scoped][Singleton])
            // appears once in EACH ForAttributeWithMetadataName pipeline, so typeInfos may contain
            // multiple entries for the same type. The transform already analyzed ALL attributes on
            // the type, so every duplicate entry is value-identical — the first one is sufficient.
            var seenTypes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var info in typeInfos)
            {
                if (!seenTypes.Add(info.TypeFullName))
                    continue;

                foreach (var diag in info.Diagnostics)
                    ctx.ReportDiagnostic(diag.ToDiagnostic());

                if (info.Registration is { } reg)
                    registrations.Add(new RegistrationRecord(
                        info.TypeFullName, reg.ServiceFullName, reg.Lifetime, reg.ServiceKey));
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

            // Process registrar candidates: replay diagnostics, collect valid ones.
            var validRegistrars = new List<string>();
            foreach (var candidate in registrars)
            {
                foreach (var diag in candidate.Diagnostics)
                    ctx.ReportDiagnostic(diag.ToDiagnostic());

                if (!candidate.HasPublicParameterlessCtor)
                    continue; // missing-ctor: diagnosed above, do not register.

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

    private static ImmutableArray<DiscoveredRegistration> CollectTypeInfoFromAttributeContext(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return ImmutableArray<DiscoveredRegistration>.Empty;
        if (ctx.TargetNode is not ClassDeclarationSyntax classDecl) return ImmutableArray<DiscoveredRegistration>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) return ImmutableArray<DiscoveredRegistration>.Empty;

        return AnalyzeType(type, classDecl);
    }

    // -------------------------------------------------------------------------
    // CollectMarkerOnlyTypeInfo: marker-interface path (CreateSyntaxProvider).
    // Skips classes that already have a DI attribute (handled by the attribute pipeline).
    // Uses per-node SemanticModel — no CompilationProvider needed.
    // -------------------------------------------------------------------------

    private static ImmutableArray<DiscoveredRegistration> CollectMarkerOnlyTypeInfo(
        GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl) return ImmutableArray<DiscoveredRegistration>.Empty;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type)
            return ImmutableArray<DiscoveredRegistration>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class) return ImmutableArray<DiscoveredRegistration>.Empty;

        // Skip if already handled by the attribute pipeline.
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass is null) continue;
            var (lifetime, _) = ResolveAttributeLifetimeWithLegacyFlag(attr.AttributeClass);
            if (lifetime is not null) return ImmutableArray<DiscoveredRegistration>.Empty;
        }

        // Only emit if there is at least one marker lifetime.
        var markerLifetimes = CollectMarkerLifetimes(type);
        if (markerLifetimes.Count == 0) return ImmutableArray<DiscoveredRegistration>.Empty;

        return AnalyzeType(type, classDecl);
    }

    // -------------------------------------------------------------------------
    // AnalyzeType: shared logic for both attribute and marker paths. Runs the FULL
    // semantic analysis (former ProcessTypeInfo + CollectTypeInfoCore) and returns an
    // equatable DiscoveredRegistration carrying only strings/value types + captured
    // diagnostics — no symbols/syntax cross the pipeline boundary.
    // -------------------------------------------------------------------------

    private static ImmutableArray<DiscoveredRegistration> AnalyzeType(
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
            return ImmutableArray<DiscoveredRegistration>.Empty;

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

        var typeFullName = ToGlobalQualified(type);
        var registration = BuildRegistration(
            type,
            classDecl,
            typeFullName,
            lifetimeAttrs,
            markerLifetimes,
            genericMarkerServiceType,
            multipleGenericMarkerServiceTypes,
            attributeServiceType,
            allowSelfRegistration,
            serviceKey);

        return ImmutableArray.Create(registration);
    }

    // -------------------------------------------------------------------------
    // BuildRegistration: apply conflict rules, capture diagnostics, resolve the
    // registration. Returns an equatable record; diagnostics are captured as
    // DiagnosticInfo (file + spans) so the output stage can rebuild Location.
    // (Former ProcessTypeInfo — every ctx.ReportDiagnostic(...) becomes a captured
    // DiagnosticInfo, and every early `return;` becomes a diagnostic-only result.)
    // -------------------------------------------------------------------------

    private static DiscoveredRegistration BuildRegistration(
        INamedTypeSymbol type,
        ClassDeclarationSyntax classDecl,
        string typeFullName,
        List<(AttributeData Attr, string Lifetime, bool IsLegacy)> lifetimeAttrs,
        List<string> markerLifetimes,
        INamedTypeSymbol? genericMarkerServiceType,
        bool multipleGenericMarkerServiceTypes,
        INamedTypeSymbol? attributeServiceType,
        bool allowSelfRegistration,
        string? serviceKey)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var classLocation = classDecl.Identifier.GetLocation();
        var typeFullDisplayName = type.ToDisplayString();

        DiscoveredRegistration DiagnosticsOnly() =>
            new(typeFullName, null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));

        // THEMIA001: Multiple lifetime attributes.
        if (lifetimeAttrs.Count > 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.MultipleLifetimeAttributes, classLocation, typeFullDisplayName));
            return DiagnosticsOnly();
        }

        // THEMIA002: Multiple lifetime markers (distinct lifetimes).
        if (markerLifetimes.Count > 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.MultipleLifetimeMarkers, classLocation, typeFullDisplayName));
            return DiagnosticsOnly();
        }

        // THEMIA005: Multiple generic markers with different TService — the service type is
        // ambiguous, so don't silently pick one.
        if (multipleGenericMarkerServiceTypes)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.AmbiguousServiceType, classLocation, typeFullDisplayName));
            return DiagnosticsOnly();
        }

        var attrLifetimeStr = lifetimeAttrs.Count == 1 ? lifetimeAttrs[0].Lifetime : null;
        var markerLifetimeStr = markerLifetimes.Count == 1 ? markerLifetimes[0] : null;

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
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.AttributeMarkerDisagreement, classLocation, typeFullDisplayName));
                    return DiagnosticsOnly();
                case LifetimeConflict.Redundant:
                    // THEMIA004: Redundant — warn but continue with attribute lifetime.
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.RedundantLifetimeAttributeAndMarker, classLocation, typeFullDisplayName));
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
        if (lifetimeAttrs.Count == 1)
        {
            switch (lifetimeAttrs[0].IsLegacy)
            {
                case true:
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.LegacyAttributeUsage, classLocation, typeFullDisplayName));
                    // continue emission
                    break;
            }
        }

        // THEMIA007: Attribute ServiceType conflicts with generic marker service type.
        if (attributeServiceType is not null && genericMarkerServiceType is not null)
        {
            var attrSvcFull = attributeServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var markerSvcFull = genericMarkerServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (attrSvcFull != markerSvcFull)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.AttributeServiceTypeConflictsWithGenericMarker, classLocation, typeFullDisplayName));
                return DiagnosticsOnly();
            }
        }

        // Determine final service type.
        INamedTypeSymbol? serviceType;

        if (attributeServiceType is not null)
        {
            serviceType = attributeServiceType;
        }
        else if (genericMarkerServiceType is not null)
        {
            serviceType = genericMarkerServiceType;
        }
        else
        {
            // Fall back to the I{ClassName} convention, then self-registration if the
            // attribute opted in via AllowSelfRegistration.
            ServiceTypeResolver.TryResolveWithSelfRegistration(
                type, allowSelfRegistration, out serviceType);
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
                var candidateInterfaces = type.Interfaces
                    .Where(i => !IsMarkerInterface(i))
                    .ToList();

                if (candidateInterfaces.Count > 1)
                {
                    // THEMIA005: Ambiguous service type.
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.AmbiguousServiceType, classLocation, typeFullDisplayName));
                }
                else
                {
                    // THEMIA006: Cannot register — no service interface.
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.CannotRegister, classLocation,
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
                var candidateInterfaces = type.Interfaces
                    .Where(i => !IsMarkerInterface(i))
                    .ToList();

                if (candidateInterfaces.Count > 1)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.AmbiguousServiceType, classLocation, typeFullDisplayName));
                }
                else
                {
                    // No I{ClassName} interface and AllowSelfRegistration is false — the same end
                    // state the marker-only path above diagnoses, so emit THEMIA006 instead of
                    // skipping silently (an explicit [Scoped] otherwise vanishes with no feedback).
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.CannotRegister, classLocation,
                        typeFullDisplayName,
                        "no matching service interface found and AllowSelfRegistration is false"));
                }
            }
            return DiagnosticsOnly();
        }

        return new DiscoveredRegistration(
            typeFullName,
            new RegistrationData(ToGlobalQualified(serviceType), resolvedLifetimeStr!, serviceKey),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray()));
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
    // Uses per-node SemanticModel — no CompilationProvider needed. Captures the
    // registrar's missing-ctor / internal diagnostics as DiagnosticInfo so the
    // output stage stays symbol-free.
    // -------------------------------------------------------------------------

    private static ImmutableArray<RegistrarInfo> DiscoverRegistrarCandidatesFromContext(
        GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl)
            return ImmutableArray<RegistrarInfo>.Empty;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol type)
            return ImmutableArray<RegistrarInfo>.Empty;
        if (type.IsAbstract || type.TypeKind != TypeKind.Class)
            return ImmutableArray<RegistrarInfo>.Empty;

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

        if (!isRegistrar) return ImmutableArray<RegistrarInfo>.Empty;

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
        var name = type.Name;

        var diagnostics = new List<DiagnosticInfo>();
        if (!hasParameterlessCtor)
        {
            // THEMIA008: registrar missing a public parameterless constructor.
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.RegistrarMissingPublicCtor, location, name));
        }
        else if (isInternal)
        {
            // THEMIA009: internal registrar — warn but still emit the registration.
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.RegistrarIsInternal, location, name));
        }

        return ImmutableArray.Create(new RegistrarInfo(
            GlobalQualifiedName: ToGlobalQualified(type),
            HasPublicParameterlessCtor: hasParameterlessCtor,
            IsInternal: isInternal,
            Diagnostics: new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutableArray())));
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

    private sealed class RegistrationRecord(string implementationFullName, string serviceFullName, string lifetime, string? key)
    {
        public string ImplementationFullName { get; } = implementationFullName;
        public string ServiceFullName { get; } = serviceFullName;
        public string Lifetime { get; } = lifetime;
        public string? Key { get; } = key;
    }
}
