using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA105: flags exception text (Message, ToString(), or the exception itself as a value) passed as a logging argument.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExceptionMessageInLogAnalyzer : DiagnosticAnalyzer
{
    private const string LoggingNamespace = "Microsoft.Extensions.Logging";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.ExceptionMessageInLog];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var exception = start.Compilation.GetTypeByMetadataName("System.Exception");
            var logger = start.Compilation.GetTypeByMetadataName(LoggingNamespace + ".ILogger");
            if (exception is null || logger is null)
                return;

            var symbols = new Symbols(exception, logger, start.Compilation.GetTypeByMetadataName(LoggingNamespace + ".LoggerExtensions"),
                start.Compilation.GetTypeByMetadataName(LoggingNamespace + ".LoggerMessageAttribute"));
            start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, symbols), OperationKind.Invocation);
        });
    }

    private sealed class Symbols
    {
        public Symbols(INamedTypeSymbol exception, INamedTypeSymbol logger, INamedTypeSymbol? extensions, INamedTypeSymbol? messageAttribute)
        {
            Exception = exception;
            Logger = logger;
            Extensions = extensions;
            MessageAttribute = messageAttribute;
        }

        public INamedTypeSymbol Exception { get; }

        public INamedTypeSymbol Logger { get; }

        public INamedTypeSymbol? Extensions { get; }

        public INamedTypeSymbol? MessageAttribute { get; }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Symbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!IsLoggingCall(invocation.TargetMethod, symbols))
            return;

        foreach (var argument in invocation.Arguments)
        {
            // The exception argument itself is the allowed place for the exception object.
            if (argument.Parameter is { } parameter && DerivesFromException(parameter.Type, symbols))
                continue;
            Visit(argument.Value, isReceiver: false, symbols, context);
        }
    }

    private static bool IsLoggingCall(IMethodSymbol method, Symbols symbols)
    {
        var container = method.ContainingType;
        if (symbols.Extensions is not null && SymbolEqualityComparer.Default.Equals(container, symbols.Extensions))
            return true;
        if (SymbolEqualityComparer.Default.Equals(container, symbols.Logger))
            return true;
        if (HasLoggerMessageAttribute(method, symbols))
            return true;

        // LoggerMessage.Define(...) returns Action<ILogger, T1.., Exception?>; its Invoke is the logging call.
        return method.MethodKind == MethodKind.DelegateInvoke
            && container.Name == "Action"
            && container.ContainingNamespace.ToDisplayString() == "System"
            && container.TypeArguments.Length > 0
            && SymbolEqualityComparer.Default.Equals(container.TypeArguments[0], symbols.Logger);
    }

    // A [LoggerMessage] partial method carries the attribute on its definition part.
    private static bool HasLoggerMessageAttribute(IMethodSymbol method, Symbols symbols)
    {
        if (symbols.MessageAttribute is null)
            return false;
        var declaration = method.PartialDefinitionPart ?? method;
        return declaration.GetAttributes().Any(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.MessageAttribute));
    }

    private static void Visit(IOperation? operation, bool isReceiver, Symbols symbols, OperationAnalysisContext context)
    {
        if (operation is null or IAnonymousFunctionOperation or ILocalFunctionOperation)
            return;

        if (operation is IPropertyReferenceOperation { Property.Name: "Message" } message
            && message.Instance is { } owner && DerivesFromException(owner.Type, symbols))
        {
            Report(operation, context);
            return;
        }

        if (operation is IInvocationOperation { TargetMethod: { Name: "ToString", Parameters.Length: 0 } } toString
            && toString.Instance is { } target && DerivesFromException(target.Type, symbols))
        {
            Report(operation, context);
            return;
        }

        // An exception used as a value is formatted through ToString(); as a member-access receiver it is not.
        if (!isReceiver && DerivesFromException(operation.Type, symbols) && operation is not IConversionOperation)
        {
            Report(operation, context);
            return;
        }

        foreach (var child in operation.ChildOperations)
        {
            var receiver = operation switch
            {
                IPropertyReferenceOperation p => ReferenceEquals(child, p.Instance),
                IInvocationOperation i => ReferenceEquals(child, i.Instance),
                IFieldReferenceOperation f => ReferenceEquals(child, f.Instance),
                IConditionalAccessOperation c => ReferenceEquals(child, c.Operation),
                _ => false,
            };
            Visit(child, receiver, symbols, context);
        }
    }

    private static bool DerivesFromException(ITypeSymbol? type, Symbols symbols)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, symbols.Exception))
                return true;
        }

        return false;
    }

    private static void Report(IOperation operation, OperationAnalysisContext context) =>
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.ExceptionMessageInLog,
            operation.Syntax.GetLocation(),
            operation.Syntax.ToString()));
}
