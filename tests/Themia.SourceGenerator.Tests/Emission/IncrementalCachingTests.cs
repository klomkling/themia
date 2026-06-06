using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Themia.SourceGenerator.Generators;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

/// <summary>
/// Proves the DI generator achieves real incrementality: after an UNRELATED edit, the output node
/// is served from cache instead of re-running. This holds only because the pipeline carries an
/// equatable, compilation-free model (<see cref="DiscoveredRegistration"/> / <c>RegistrarInfo</c>) —
/// any leaked Roslyn symbol/syntax/Location would defeat the per-node cache and flip the reason away
/// from <see cref="IncrementalStepRunReason.Cached"/>/<see cref="IncrementalStepRunReason.Unchanged"/>.
/// </summary>
public class IncrementalCachingTests
{
    private static readonly MetadataReference ThemiaDIRef =
        MetadataReference.CreateFromFile(typeof(Themia.DependencyInjection.ScopedAttribute).Assembly.Location);

    private static CSharpCompilation CreateCompilation(SyntaxTree tree)
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .Append(ThemiaDIRef);
        return CSharpCompilation.Create(
            "ConsumerAssembly",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void OutputNode_IsCached_AfterUnrelatedEdit()
    {
        const string original = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            [Scoped]
            public class Foo : IFoo { }
            """;

        // Same registrations; only an unrelated method body is added elsewhere in the tree.
        const string edited = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            [Scoped]
            public class Foo : IFoo { }
            public static class Unrelated { public static int Compute() => 42; }
            """;

        var originalTree = CSharpSyntaxTree.ParseText(original);
        var compilation = CreateCompilation(originalTree);

        var driver = CSharpGeneratorDriver.Create(
            [new ServiceRegistrationGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

        // First run populates the cache.
        var gd = driver.RunGenerators(compilation);

        // Second run on a compilation that differs only by an unrelated declaration.
        var editedTree = CSharpSyntaxTree.ParseText(edited);
        var editedCompilation = compilation.ReplaceSyntaxTree(originalTree, editedTree);
        gd = gd.RunGenerators(editedCompilation);

        var result = gd.GetRunResult().Results[0];
        var trackedOutputs = result.TrackedOutputSteps
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .ToList();

        Assert.NotEmpty(trackedOutputs);
        Assert.All(trackedOutputs, output =>
            Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"Output node re-ran with reason '{output.Reason}' after an unrelated edit — a symbol/syntax/Location is leaking into the pipeline."));
    }
}
