using Themia.Generators.Abstractions.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Themia.Analyzers.Diagnostics;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor SwallowLogRethrow =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA101",
            "Catch logs and rethrows",
            "Catch block logs and rethrows; let the top-level handler log it instead.");

    public static readonly DiagnosticDescriptor SyncOverAsync =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA102",
            "Synchronous work wrapped in Task.FromResult",
            "'{0}' wraps synchronous work in Task.FromResult; provide a genuinely async implementation.");

    // The tenant-isolation gates get their own category so adopters can configure them as a group via
    // .editorconfig (dotnet_analyzer_diagnostic.category-Themia.Isolation.severity). Built locally rather
    // than via ThemiaDiagnostics.CreateWarning, which hardcodes the Themia.DI category.
    private const string IsolationCategory = "Themia.Isolation";

    // No ID-pattern validation (unlike ThemiaDiagnostics) — every caller below passes a compile-time
    // string literal, so a malformed ID would be caught in review, not at runtime.
    private static DiagnosticDescriptor IsolationWarning(string id, string title, string message) =>
        new(
            id,
            title,
            message,
            IsolationCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: null,
            helpLinkUri: $"https://github.com/klomkling/themia/blob/main/docs/analyzers/{id}.md");

    public static readonly DiagnosticDescriptor RawConnectionBypass = IsolationWarning(
        "THEMIA103",
        "Raw Dapper connection bypasses tenant isolation",
        "Raw Dapper connection bypasses tenant isolation; use ITenantQueryFactory.For<T>() for ad-hoc " +
        "tenant-scoped queries, or suppress THEMIA103 with a justification for a deliberate bypass.");

    public static readonly DiagnosticDescriptor DbSetFindBypass = IsolationWarning(
        "THEMIA104",
        "DbSet.Find bypasses the tenant post-check",
        "DbSet.Find/FindAsync bypasses Themia's tenant post-check for already-tracked entities; use " +
        "DbContext.FindAsync<T>() or IReadRepository.GetByIdAsync(). Suppress THEMIA104 with a " +
        "justification for a deliberate bypass.");
}
