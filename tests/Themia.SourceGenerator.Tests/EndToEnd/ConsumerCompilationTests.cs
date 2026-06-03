using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Themia.SourceGenerator.Generators;
using Xunit;

namespace Themia.SourceGenerator.Tests.EndToEnd;

/// <summary>
/// Tier-0 integration gate: proves the DI generator runs end-to-end against a real
/// consumer compilation that references the shipped <c>Themia.DependencyInjection</c>
/// attributes, and emits the expected registration. (The mediator generator is deferred
/// to Tier 2, so only the DI pipeline is asserted here.)
/// </summary>
public class ConsumerCompilationTests
{
    [Fact]
    public void Di_generator_emits_registration_for_a_real_consumer()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Consumer;
            public interface IFooService { }
            [Scoped]
            public class FooService : IFooService { }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Themia.DependencyInjection.ScopedAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ServiceRegistrationGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Select(t => t.ToString()));
        Assert.Contains(
            "services.AddScoped<global::Consumer.IFooService, global::Consumer.FooService>();",
            generated);
    }
}
