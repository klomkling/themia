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
            // The consumer (and the generated AddThemiaServices extension) needs the DI
            // attributes and IServiceCollection — reference both assemblies explicitly so the
            // output compilation is complete enough to compile-check.
            .Append(MetadataReference.CreateFromFile(
                typeof(Themia.DependencyInjection.ScopedAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));
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

        // The generated code must itself compile in the consumer — e.g. no using of a
        // namespace that does not exist. (Guards against emitting stray using directives.)
        var compileErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(compileErrors.Count == 0,
            "Generated code did not compile cleanly: " + string.Join("; ", compileErrors));
    }

    /// <summary>
    /// Regression gate for CS0121: a consumer that references an upstream assembly which
    /// already emitted a <c>public Themia.Generated.ThemiaServiceRegistrations</c> must NOT
    /// get an ambiguous-extension-method error when the generator also runs in its own
    /// compilation. After the fix the generated class is <c>internal</c>, so the two types
    /// exist in different assemblies and never collide.
    /// </summary>
    [Fact]
    public void Di_generator_emits_internal_class_no_CS0121_when_upstream_has_public_ThemiaServiceRegistrations()
    {
        // Simulate an upstream assembly that already contains a PUBLIC ThemiaServiceRegistrations
        // with AddThemiaServices — this is the exact scenario that caused CS0121 before the fix.
        const string upstreamSource = """
            using Microsoft.Extensions.DependencyInjection;
            namespace Themia.Generated;
            public static class ThemiaServiceRegistrations
            {
                public static IServiceCollection AddThemiaServices(this IServiceCollection services)
                    => services;
            }
            """;

        IEnumerable<MetadataReference> baseReferences = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Themia.DependencyInjection.ScopedAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));

        // Build the upstream assembly that contains the public class.
        var upstreamCompilation = CSharpCompilation.Create(
            "Upstream",
            [CSharpSyntaxTree.ParseText(upstreamSource)],
            baseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var upstreamRef = upstreamCompilation.ToMetadataReference();

        // Consumer compilation references the upstream assembly (with its public
        // ThemiaServiceRegistrations) and also runs the DI generator.
        const string consumerSource = """
            using Themia.DependencyInjection;
            namespace Consumer;
            public interface IBarService { }
            [Scoped]
            public class BarService : IBarService { }
            """;

        var consumerCompilation = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(consumerSource)],
            baseReferences.Append(upstreamRef),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ServiceRegistrationGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            consumerCompilation, out var outputCompilation, out var generatorDiagnostics);

        // Generator itself must not error.
        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // The generated source must use "internal static class", not "public static class".
        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Select(t => t.ToString()));
        Assert.Contains("internal static class ThemiaServiceRegistrations", generated);
        Assert.DoesNotContain("public static class ThemiaServiceRegistrations", generated);

        // The output compilation must compile without errors — in particular no CS0121.
        var compileErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(compileErrors.Count == 0,
            "CS0121 or other compile error after fix: " + string.Join("; ", compileErrors));
    }
}
