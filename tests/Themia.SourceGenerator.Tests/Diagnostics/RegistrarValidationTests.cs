using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Themia.SourceGenerator.Generators;
using Xunit;

namespace Themia.SourceGenerator.Tests.Diagnostics;

/// <summary>
/// Tests that the generator emits THEMIA008 and THEMIA009 diagnostics
/// for invalid IThemiaServiceRegistrar implementations.
/// Uses the same manual driver approach as ConflictDiagnosticTests.
/// </summary>
public class RegistrarValidationTests
{
    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var themiaDIRef = MetadataReference.CreateFromFile(
            typeof(Themia.DependencyInjection.ScopedAttribute).Assembly.Location);

        var refs = System.AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .Append(themiaDIRef);

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    private static void AssertSingleDiagnostic(
        GeneratorDriverRunResult result,
        string expectedId,
        DiagnosticSeverity expectedSeverity)
    {
        var diags = result.Diagnostics;
        var matching = diags.Where(d => d.Id == expectedId).ToList();

        Assert.True(matching.Count > 0,
            $"Expected diagnostic {expectedId} but found none. " +
            $"Actual: [{string.Join(", ", diags.Select(d => d.Id))}]");

        Assert.Equal(expectedSeverity, matching[0].Severity);
    }

    [Fact]
    public void THEMIA008_RegistrarMissingPublicCtor()
    {
        const string source = """
            using Themia.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection;
            namespace Demo;
            public class MyReg : IThemiaServiceRegistrar
            {
                public MyReg(int x) { }
                public void Register(IServiceCollection services) { }
            }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA008", DiagnosticSeverity.Error);
    }

    [Fact]
    public void THEMIA009_RegistrarIsInternal()
    {
        const string source = """
            using Themia.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection;
            namespace Demo;
            internal class MyReg : IThemiaServiceRegistrar
            {
                public void Register(IServiceCollection services) { }
            }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA009", DiagnosticSeverity.Warning);
    }
}
