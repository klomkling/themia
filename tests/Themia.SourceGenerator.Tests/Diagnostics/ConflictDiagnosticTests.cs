using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Themia.SourceGenerator.Generators;
using Xunit;

namespace Themia.SourceGenerator.Tests.Diagnostics;

/// <summary>
/// Tests that the generator emits THEMIA001–007 conflict diagnostics.
/// Uses a manual driver approach to avoid xunit version conflicts
/// with CSharpSourceGeneratorTest.
/// </summary>
public class ConflictDiagnosticTests
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
            [syntaxTree],
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
    public void THEMIA001_MultipleAttributes()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            [Scoped][Singleton]
            public class Foo : IFoo { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA001", DiagnosticSeverity.Error);
    }

    [Fact]
    public void THEMIA002_MultipleMarkers()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            public class Foo : IFoo, IScopedService, ISingletonService { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA002", DiagnosticSeverity.Error);
    }

    [Fact]
    public void THEMIA003_AttributeMarkerDisagreement()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            [Scoped]
            public class Foo : IFoo, ISingletonService { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA003", DiagnosticSeverity.Error);
    }

    [Fact]
    public void THEMIA004_RedundantAttributeAndMarker()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            [Scoped] public class Foo : IFoo, IScopedService { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA004", DiagnosticSeverity.Warning);
    }

    [Fact]
    public void THEMIA005_AmbiguousServiceType()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IBar { }
            public interface IBaz { }
            public class Foo : IBar, IBaz, IScopedService { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA005", DiagnosticSeverity.Warning);
    }

    [Fact]
    public void THEMIA006_CannotRegister_NoServiceInterfaceNoSelf()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public class Foo : IScopedService { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA006", DiagnosticSeverity.Warning);
    }

    [Fact]
    public void THEMIA007_AttributeServiceTypeVsGenericMarker()
    {
        const string source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            public interface IBar { }
            [Scoped(ServiceType = typeof(IFoo))]
            public class Foo : IFoo, IBar, IScopedService<IBar> { }
            """;

        var result = RunGenerator(source);
        AssertSingleDiagnostic(result, "THEMIA007", DiagnosticSeverity.Error);
    }
}
