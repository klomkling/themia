using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA104: flags DbSet&lt;T&gt;.Find/FindAsync, which bypasses ThemiaDbContext's tenant
/// post-check for already-tracked entities. Steers callers to DbContext.FindAsync&lt;T&gt;() /
/// IReadRepository.GetByIdAsync(). Silent inside the Themia.Framework.Data.* assemblies.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DbSetFindBypassAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.DbSetFindBypass];

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

        // GetTypesByMetadataName (not the singular GetTypeByMetadataName, which returns null when the type
        // is defined in more than one referenced assembly) so the gate doesn't silently disengage in a graph
        // that surfaces DbSet<T> from multiple assemblies.
        var dbSets = context.Compilation.GetTypesByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
        if (dbSets.IsEmpty)
            return; // EF Core not referenced — nothing to flag.

        context.RegisterOperationAction(ctx => Analyze(ctx, dbSets), OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context, ImmutableArray<INamedTypeSymbol> dbSets)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name is not ("Find" or "FindAsync"))
            return;
        // Matches when the declaring type is DbSet<T> — covers `dbSet.Find(...)`, `ctx.Set<T>().Find(...)`,
        // and a subclass that inherits Find without overriding it. Known limitation: a subclass that
        // *overrides* Find escapes this (ContainingType becomes the subclass). That is vanishingly rare
        // (DbSet<T> is abstract; EF supplies the concrete type) and the data layer self-exempts anyway.
        var containing = method.ContainingType.OriginalDefinition;
        foreach (var dbSet in dbSets)
        {
            if (SymbolEqualityComparer.Default.Equals(containing, dbSet))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DbSetFindBypass, context.Operation.Syntax.GetLocation()));
                return;
            }
        }
    }
}
