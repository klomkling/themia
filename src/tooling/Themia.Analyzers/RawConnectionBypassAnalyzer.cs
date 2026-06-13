using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA103: flags IDapperConnectionContext.GetOpenConnectionAsync — the raw-connection bypass of
/// tenant isolation. Steers callers to ITenantQueryFactory.For&lt;T&gt;(). Silent inside the
/// Themia.Framework.Data.* assemblies.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RawConnectionBypassAnalyzer : DiagnosticAnalyzer
{
    private const string ConnectionContextMetadataName =
        "Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.RawConnectionBypass];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (DataLayerScope.IsDataLayerAssembly(context.Compilation.AssemblyName))
            return;

        var contextType = context.Compilation.GetTypeByMetadataName(ConnectionContextMetadataName);
        if (contextType is null)
            return; // Dapper data layer not referenced.

        context.RegisterOperationAction(ctx => Analyze(ctx, contextType), OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol contextType)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name != "GetOpenConnectionAsync")
            return;
        // Matches calls through the IDapperConnectionContext interface — the only surface adopters see
        // (it is DI-injected; the concrete implementation is internal to the Dapper data layer). A call
        // through a concrete-type variable would have a different ContainingType and escape this, but that
        // type is inaccessible outside the data layer, which self-exempts anyway.
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, contextType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.RawConnectionBypass, context.Operation.Syntax.GetLocation()));
    }
}
