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
}
