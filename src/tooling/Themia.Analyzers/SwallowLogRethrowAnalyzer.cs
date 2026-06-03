using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA101: flags catch blocks that log and rethrow.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwallowLogRethrowAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.SwallowLogRethrow];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.CatchClause);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var catchClause = (CatchClauseSyntax)context.Node;
        if (catchClause.Block is null)
            return;

        // A rethrow of the CAUGHT exception: `throw;`, or `throw ex;` where `ex` is the catch
        // variable (`throw other;` of an unrelated variable is NOT a rethrow). It may sit inside
        // a nested if/try/using block — scan descendants — but skip nested lambdas and local
        // functions, where a throw is not a rethrow of the caught exception. (Mirrors the deep
        // scan used for the logger-call check below.)
        var caughtVariable = catchClause.Declaration?.Identifier.Text;
        var rethrows = catchClause.Block
            .DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<ThrowStatementSyntax>()
            .Any(t => t.Expression is null
                || (t.Expression is IdentifierNameSyntax id
                    && !string.IsNullOrEmpty(caughtVariable)
                    && id.Identifier.Text == caughtVariable));
        if (!rethrows)
            return;

        // Same descendant scan + lambda/local-function exclusion as the rethrow check above:
        // a LogError call inside a nested lambda is not part of the catch's log-and-rethrow.
        var logs = catchClause.Block
            .DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => IsLoggerCall(inv, context.SemanticModel));
        if (!logs)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.SwallowLogRethrow,
            catchClause.CatchKeyword.GetLocation()));
    }

    private static bool IsLoggerCall(InvocationExpressionSyntax inv, SemanticModel model)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax ma)
            return false;
        var name = ma.Name.Identifier.Text;
        if (name != "LogError" && name != "LogCritical")
            return false;

        // Unresolved-receiver posture: lean toward flagging. A LogError/LogCritical
        // call whose receiver type can't bind is treated as a logger call, so a real
        // log-and-rethrow isn't missed on an incomplete compilation.
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType is null)
            return true;
        return receiverType.Name.Contains("Logger")
               || receiverType.AllInterfaces.Any(i => i.Name.Contains("Logger"));
    }
}
