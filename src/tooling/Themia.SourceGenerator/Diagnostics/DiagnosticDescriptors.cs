using Microsoft.CodeAnalysis;
using Themia.Generators.Abstractions.Diagnostics;

namespace Themia.SourceGenerator.Diagnostics;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor MultipleLifetimeAttributes =
        ThemiaDiagnostics.CreateError(
            "THEMIA001",
            "Multiple lifetime attributes",
            "Type '{0}' has more than one lifetime attribute applied. Use exactly one.");

    public static readonly DiagnosticDescriptor MultipleLifetimeMarkers =
        ThemiaDiagnostics.CreateError(
            "THEMIA002",
            "Multiple lifetime marker interfaces",
            "Type '{0}' implements more than one lifetime marker interface. Use exactly one.");

    public static readonly DiagnosticDescriptor AttributeMarkerDisagreement =
        ThemiaDiagnostics.CreateError(
            "THEMIA003",
            "Attribute and marker lifetime disagree",
            "Type '{0}' has a lifetime attribute and a lifetime marker interface that specify different lifetimes.");

    public static readonly DiagnosticDescriptor RedundantLifetimeAttributeAndMarker =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA004",
            "Redundant lifetime attribute and marker",
            "Type '{0}' specifies the same lifetime via both an attribute and a marker interface. Remove one.");

    public static readonly DiagnosticDescriptor AmbiguousServiceType =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA005",
            "Ambiguous service type",
            "Type '{0}' implements multiple candidate service interfaces. Specify the service type explicitly.");

    public static readonly DiagnosticDescriptor CannotRegister =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA006",
            "Cannot register type",
            "Type '{0}' cannot be registered: {1}.");

    public static readonly DiagnosticDescriptor AttributeServiceTypeConflictsWithGenericMarker =
        ThemiaDiagnostics.CreateError(
            "THEMIA007",
            "Attribute service type conflicts with generic marker",
            "Type '{0}' specifies a service type via attribute that conflicts with the service type implied by the generic lifetime marker interface.");

    public static readonly DiagnosticDescriptor RegistrarMissingPublicCtor =
        ThemiaDiagnostics.CreateError(
            "THEMIA008",
            "Registrar missing public constructor",
            "Type '{0}' is used as a registrar but has no accessible public constructor.");

    public static readonly DiagnosticDescriptor RegistrarIsInternal =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA009",
            "Registrar is internal",
            "Type '{0}' is an internal registrar. Consider making it public so consumers can invoke it.");

    public static readonly DiagnosticDescriptor LegacyAttributeUsage =
        ThemiaDiagnostics.CreateError(
            "THEMIA010",
            "Legacy attribute usage",
            "Type '{0}' uses a legacy lifetime attribute. Migrate to the current attribute. " +
            "This diagnostic fails the build by default; downgrade via .editorconfig " +
            "(dotnet_diagnostic.THEMIA010.severity = warning) to emit but not fail.");

    // Mediator handler generator diagnostics. These use the dedicated "ThemiaMediator"
    // category (distinct from the DI generator's "Themia.DI") and IDs THEMIA011-013 within
    // the source-generator range (THEMIA001-099) — kept clear of the analyzers' reserved
    // THEMIA100-199 range — so they are constructed directly rather than via ThemiaDiagnostics
    // (which pins the DI category).
    private const string MediatorCategory = "ThemiaMediator";

    public static readonly DiagnosticDescriptor DuplicateHandler = new(
        id: "THEMIA011",
        title: "Duplicate handler registration",
        messageFormat: "Handler '{0}' implements the same interface as '{1}' for request type '{2}' and response type '{3}'",
        category: MediatorCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Multiple handlers cannot be registered for the same request/response/kind combination.");

    public static readonly DiagnosticDescriptor OpenGenericHandler = new(
        id: "THEMIA012",
        title: "Open generic handler not supported",
        messageFormat: "Handler '{0}' contains unbound generic parameters and cannot be registered",
        category: MediatorCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Handlers with open generic parameters cannot be automatically registered. Use closed generic types.");

    public static readonly DiagnosticDescriptor HandlerNotAccessible = new(
        id: "THEMIA013",
        title: "Handler not accessible for dependency injection",
        messageFormat: "Handler '{0}' is not accessible (must be public or internal, and not a private nested class)",
        category: MediatorCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Handlers must be accessible to the dependency injection container.");
}
