using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Themia.SourceGenerator.Generators;

// MediatorHandlerGenerator lives in Themia.SourceGenerator.Generators.
namespace Themia.SourceGenerator.Tests.Helpers;

/// <summary>
/// Helper for testing the folded mediator handler generator. Runs the mediator generator
/// in isolation over a self-contained compilation that inlines the mediator infrastructure
/// types (so no real <c>Themia.Mediator</c> assembly reference is needed). The DI generator
/// is intentionally NOT included here: it is always-on (emits the <c>AddThemiaServices</c>
/// wrapper unconditionally) and would pollute the mediator pipeline's emit/diagnostic
/// assertions. The DI generator has its own dedicated test suite.
/// </summary>
public static class MediatorGeneratorTestHelper
{
    /// <summary>
    /// Runs the <see cref="MediatorHandlerGenerator"/> on the provided source code and
    /// returns the run result.
    /// </summary>
    public static GeneratorDriverRunResult RunGenerator(string source, params string[] additionalSources)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var additionalTrees = additionalSources.Select(static s => CSharpSyntaxTree.ParseText(s)).ToArray();

        // Build references from the runtime's trusted-platform-assemblies (a complete, deterministic
        // set independent of which assemblies happen to be loaded) and EXCLUDE every real Themia.*
        // assembly. This compilation inlines its own Themia.Mediator.* / Themia.DependencyInjection.*
        // infrastructure (see GetMediatorInfrastructureCode); referencing the real assemblies too would
        // create CS0433 duplicate-type conflicts that degrade symbol binding non-deterministically
        // (e.g. attribute lifetime resolution) depending on load order/platform.
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator);
        var references = trustedAssemblies
            .Where(static path => !string.IsNullOrEmpty(path))
            .Where(static path => !Path.GetFileNameWithoutExtension(path).StartsWith("Themia.", StringComparison.Ordinal))
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { syntaxTree }.Concat(additionalTrees),
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var mediatorGenerator = new MediatorHandlerGenerator();

        var driver = CSharpGeneratorDriver.Create(mediatorGenerator)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult();
    }

    /// <summary>
    /// Creates a minimal set of mediator infrastructure code for testing. The types are
    /// inlined under the real <c>Themia.Mediator.*</c> / <c>Themia.DependencyInjection</c>
    /// namespaces so the folded generator matches them by fully-qualified name.
    /// </summary>
    public static string GetMediatorInfrastructureCode()
    {
        return @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

// ServiceLifetime enum is provided by Microsoft.Extensions.DependencyInjection (already referenced)

namespace Themia.Mediator
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class GenerateMediatorHandlersAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SingletonHandlerAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class TransientHandlerAttribute : Attribute { }
}

namespace Themia.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SingletonAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ScopedAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class TransientAttribute : Attribute { }
}

namespace Themia.Mediator.Abstractions
{
    public interface IRequest<TResponse> { }
    public interface ICommand<TResponse> : IRequest<TResponse> { }
    public interface IQuery<TResponse> : IRequest<TResponse> { }

    public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
    }

    public interface ICommandHandler<TCommand, TResponse> : IRequestHandler<TCommand, TResponse> where TCommand : ICommand<TResponse> { }
    public interface IQueryHandler<TQuery, TResponse> : IRequestHandler<TQuery, TResponse> where TQuery : IQuery<TResponse> { }

    public interface IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse> { }

    public interface IMediator
    {
        Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
    }
}

namespace Themia.Mediator.Pipelines
{
    public delegate Task<TResponse> RequestHandlerContinuation<TResponse>(CancellationToken cancellationToken);
}
";
    }
}
