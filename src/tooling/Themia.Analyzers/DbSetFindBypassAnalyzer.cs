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

        var dbSet = context.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
        if (dbSet is null)
            return; // EF Core not referenced — nothing to flag.

        context.RegisterOperationAction(ctx => Analyze(ctx, dbSet), OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol dbSet)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name is not ("Find" or "FindAsync"))
            return;
        // Matches when the declaring type is DbSet<T> — covers `dbSet.Find(...)`, `ctx.Set<T>().Find(...)`,
        // and a subclass that inherits Find without overriding it. Known limitation: a subclass that
        // *overrides* Find escapes this (ContainingType becomes the subclass). That is vanishingly rare
        // (DbSet<T> is abstract; EF supplies the concrete type) and the data layer self-exempts anyway.
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, dbSet))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DbSetFindBypass, context.Operation.Syntax.GetLocation()));
    }
}
