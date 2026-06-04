using System.Linq;
using Themia.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Themia.SourceGenerator.Tests;

/// <summary>
/// End-to-end tests for the folded mediator handler generator. These drive the generator
/// over self-contained sample compilations and assert on <c>Diagnostics</c>/<c>GeneratedTrees</c>
/// directly (no Verify snapshots). The <c>Assert.Empty(result.Diagnostics)</c> gate proves the
/// emitted code is error-free, i.e. the dispatcher/registration compile.
/// </summary>
public class MediatorHandlerGeneratorTests
{
    [Fact]
    public void Generator_WithOptInAttribute_GeneratesDispatcher()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode();

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.True(result.GeneratedTrees.Length > 0);

        var dispatcherGenerated = result.GeneratedTrees.Any(t => t.FilePath.Contains("Dispatcher"));
        Assert.True(dispatcherGenerated, "Dispatcher should be generated");
    }

    [Fact]
    public void Generator_WithoutOptInAttribute_DoesNotGenerate()
    {
        // Arrange
        var source = MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"
namespace TestNamespace
{
    public class SomeClass { }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void Generator_WithCommandHandler_GeneratesHandlerRegistration()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record CreateOrderCommand(string Name) : ICommand<int>;

    public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, int>
    {
        public Task<int> HandleAsync(CreateOrderCommand request, CancellationToken cancellationToken)
        {
            return Task.FromResult(42);
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.True(result.GeneratedTrees.Length > 0);

        var handlerFileGenerated = result.GeneratedTrees.Any(t =>
            t.FilePath.Contains("Handler") && t.FilePath.Contains("CreateOrderHandler"));
        Assert.True(handlerFileGenerated, "Handler file should be generated");

        var registrationGenerated = result.GeneratedTrees.Any(t => t.FilePath.Contains("Registration"));
        Assert.True(registrationGenerated, "Registration file should be generated");
    }

    [Fact]
    public void Generator_WithQueryHandler_GeneratesHandlerRegistration()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetOrderQuery(int Id) : IQuery<string>;

    public class GetOrderHandler : IQueryHandler<GetOrderQuery, string>
    {
        public Task<string> HandleAsync(GetOrderQuery request, CancellationToken cancellationToken)
        {
            return Task.FromResult(""Order"");
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.True(result.GeneratedTrees.Length > 0);
    }

    [Fact]
    public void Generator_WithAbstractHandler_DoesNotGenerateHandler()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record TestCommand : ICommand<int>;

    public abstract class AbstractHandler : ICommandHandler<TestCommand, int>
    {
        public abstract Task<int> HandleAsync(TestCommand request, CancellationToken cancellationToken);
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        // Should only generate dispatcher, not handler files
        var handlerFileGenerated = result.GeneratedTrees.Any(t =>
            t.FilePath.Contains("Handler") && t.FilePath.Contains("AbstractHandler"));
        Assert.False(handlerFileGenerated, "Abstract handler should not be generated");
    }

    [Fact]
    public void Generator_WithOpenGenericHandler_ReportsDiagnostic()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GenericCommand<T> : ICommand<T>;

    public class GenericHandler<T> : ICommandHandler<GenericCommand<T>, T>
    {
        public Task<T> HandleAsync(GenericCommand<T> request, CancellationToken cancellationToken)
        {
            return Task.FromResult(default(T)!);
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        var openGenericDiagnostic = result.Diagnostics.Any(d => d.Id == "THEMIA012");
        Assert.True(openGenericDiagnostic, "Should report THEMIA012 for open generic handler");
    }

    [Fact]
    public void Generator_WithNonHandlerOpenGenericType_DoesNotReportDiagnostic()
    {
        // Arrange — an open-generic, accessible type that is NOT a handler must not trip
        // THEMIA012/THEMIA013 (regression: discovery used to validate before checking
        // IRequestHandler<,>, flagging benign consumer types).
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    // Not a handler — just an unrelated open-generic helper in the consumer's assembly.
    public sealed class Box<T>
    {
        public T? Value { get; set; }
    }

    public record PingCommand : ICommand<string>;

    public class PingHandler : ICommandHandler<PingCommand, string>
    {
        public Task<string> HandleAsync(PingCommand request, CancellationToken cancellationToken)
            => Task.FromResult(""pong"");
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert — no handler diagnostics fire for the non-handler open generic
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "THEMIA012");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "THEMIA013");
    }

    [Fact]
    public void Generator_WithPipelineBehavior_DoesNotGenerateAsHandler()
    {
        // Arrange
        var source = MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using Themia.Mediator.Pipelines;
    using System.Threading;
    using System.Threading.Tasks;

    public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> HandleAsync(TRequest request, RequestHandlerContinuation<TResponse> next, CancellationToken cancellationToken)
        {
            return next(cancellationToken);
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        var behaviorFileGenerated = result.GeneratedTrees.Any(t =>
            t.FilePath.Contains("LoggingBehavior"));
        Assert.False(behaviorFileGenerated, "Pipeline behavior should not be generated as handler");
    }

    [Fact]
    public void Generator_WithLifetimeAttribute_GeneratesCorrectLifetime()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using Themia.DependencyInjection;
    using System.Threading;
    using System.Threading.Tasks;

    public record TestCommand : ICommand<int>;

    [Singleton]
    public class TestHandler : ICommandHandler<TestCommand, int>
    {
        public Task<int> HandleAsync(TestCommand request, CancellationToken cancellationToken)
        {
            return Task.FromResult(1);
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        var handlerFile = result.GeneratedTrees.FirstOrDefault(t =>
            t.FilePath.Contains("Handler") && t.FilePath.Contains("TestHandler"));
        Assert.NotNull(handlerFile);

        var generatedCode = handlerFile!.ToString();
        Assert.Contains("AddSingleton", generatedCode);
    }

    [Fact]
    public void Generator_WithPrivateNestedHandler_ReportsDiagnostic()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record TestCommand : ICommand<int>;

    public class OuterClass
    {
        private class PrivateHandler : ICommandHandler<TestCommand, int>
        {
            public Task<int> HandleAsync(TestCommand request, CancellationToken cancellationToken)
            {
                return Task.FromResult(1);
            }
        }
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        var accessibilityDiagnostic = result.Diagnostics.Any(d => d.Id == "THEMIA013");
        Assert.True(accessibilityDiagnostic, "Should report THEMIA013 for inaccessible handler");
    }

    [Fact]
    public void Generator_GeneratesDispatcherWithCorrectSignature()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode();

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        var dispatcherFile = result.GeneratedTrees.FirstOrDefault(t => t.FilePath.Contains("Dispatcher"));
        Assert.NotNull(dispatcherFile);

        var generatedCode = dispatcherFile!.ToString();
        Assert.Contains("class MediatorDispatcher : IMediator", generatedCode);
        Assert.Contains("SendAsync", generatedCode);
        Assert.DoesNotContain("System.Reflection", generatedCode);
    }

    [Fact]
    public void Generator_WithOptInAndNoHandlers_GeneratesRegistrationStub()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    // No handlers yet
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Empty(result.Diagnostics);
        var registrationFile = result.GeneratedTrees.FirstOrDefault(t => t.FilePath.Contains("Registration"));
        Assert.NotNull(registrationFile);
        Assert.Contains("AddGeneratedMediatorHandlers", registrationFile!.ToString());
    }

    [Fact]
    public void Generator_WithDuplicateHandlers_ReportsDiagnostic()
    {
        // Arrange
        var source = @"
[assembly: Themia.Mediator.GenerateMediatorHandlers]
" + MediatorGeneratorTestHelper.GetMediatorInfrastructureCode() + @"

namespace TestNamespace
{
    using Themia.Mediator.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record PingRequest : IRequest<string>;

    public class FirstPingHandler : IRequestHandler<PingRequest, string>
    {
        public Task<string> HandleAsync(PingRequest request, CancellationToken cancellationToken)
            => Task.FromResult(""first"");
    }

    public class SecondPingHandler : IRequestHandler<PingRequest, string>
    {
        public Task<string> HandleAsync(PingRequest request, CancellationToken cancellationToken)
            => Task.FromResult(""second"");
    }
}
";

        // Act
        var result = MediatorGeneratorTestHelper.RunGenerator(source);

        // Assert
        Assert.Contains(result.Diagnostics, d => d.Id == "THEMIA011");
    }
}
