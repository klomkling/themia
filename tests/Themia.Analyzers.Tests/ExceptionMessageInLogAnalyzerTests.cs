using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class ExceptionMessageInLogAnalyzerTests
{
    // The real Microsoft.Extensions.Logging shapes the rule matches on: LoggerExtensions, ILogger.Log / BeginScope,
    // a [LoggerMessage] partial method, and the LoggerMessage.Define delegate.
    private const string Stubs = @"#nullable enable
namespace Microsoft.Extensions.Logging {
    public enum LogLevel { Information }
    public interface ILogger {
        void Log<TState>(LogLevel level, System.Exception? exception, TState state, System.Func<TState, System.Exception?, string> formatter);
        System.IDisposable? BeginScope<TState>(TState state);
    }
    public interface ILogger<T> : ILogger { }
    public static class LoggerExtensions {
        public static void LogError(this ILogger l, System.Exception? e, string m, params object?[] args) { }
        public static void LogError(this ILogger l, string m, params object?[] args) { }
        public static void LogWarning(this ILogger l, string m, params object?[] args) { }
        public static void LogInformation(this ILogger l, string m, params object?[] args) { }
        public static System.IDisposable? BeginScope(this ILogger l, string m, params object?[] args) => null;
    }
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class LoggerMessageAttribute : System.Attribute {
        public LoggerMessageAttribute(int eventId, LogLevel level, string message) { }
    }
    public static class LoggerMessage {
        public static System.Action<ILogger, T1, System.Exception?> Define<T1>(LogLevel level, int id, string format) => (l, a, e) => { };
    }
}
";

    private const string Types = @"
namespace App {
    using System;
    using Microsoft.Extensions.Logging;

    public class MyException : Exception { public override string Message => ""custom""; }
    public class Other { public string Message => """"; }
    public class FakeLogger { public void LogError(string a, string b) { } }

    public static partial class Log {
        [LoggerMessage(1, LogLevel.Information, ""x {M}"")]
        public static partial void Failed(ILogger logger, string m);
        public static partial void Failed(ILogger logger, string m) { }
    }

    public class S {
        private ILogger _log = null!;
        private ILogger<S> _typed = null!;
        private static readonly Action<ILogger, string, Exception?> Defined = LoggerMessage.Define<string>(LogLevel.Information, 1, ""x {M}"");
        public void M(Exception ex) {
";

    private const string Tail = @"
        }
    }
}";

    private static string Source(string body) => Stubs + Types + body + Tail;

    private static Task Flagged(string body, int count = 1)
    {
        var test = new Verify<ExceptionMessageInLogAnalyzer>.Test { TestCode = Source(body) };
        for (var i = 0; i < count; i++)
            test.ExpectedDiagnostics.Add(new DiagnosticResult("THEMIA105", DiagnosticSeverity.Warning).WithLocation(i));
        return test.RunAsync();
    }

    private static Task Clean(string body) =>
        new Verify<ExceptionMessageInLogAnalyzer>.Test { TestCode = Source(body) }.RunAsync();

    [Fact]
    public Task MessageAsTemplateArgument_Flagged() => Flagged(@"_log.LogError(ex, ""x {M}"", {|#0:ex.Message|});");

    [Fact]
    public Task MessageInConcatenation_Flagged() => Flagged(@"_log.LogWarning(""x "" + {|#0:ex.Message|});");

    [Fact]
    public Task MessageInInterpolation_Flagged() => Flagged(@"_log.LogInformation($""x { {|#0:ex.Message|} }"");");

    [Fact]
    public Task ToStringCall_Flagged() => Flagged(@"_log.LogError(""x {M}"", {|#0:ex.ToString()|});");

    [Fact]
    public Task InnerExceptionMessage_Flagged() => Flagged(@"_log.LogError(""x {M}"", {|#0:ex.InnerException!.Message|});");

    [Fact]
    public Task BaseExceptionMessage_Flagged() => Flagged(@"_log.LogError(""x {M}"", {|#0:ex.GetBaseException().Message|});");

    [Fact]
    public Task OverriddenMessage_Flagged() => Flagged(@"var my = new MyException(); _log.LogError(""x {M}"", {|#0:my.Message|});");

    [Fact]
    public Task MessageInsideStringFormat_Flagged() => Flagged(@"_log.LogError(string.Format(""{0}"", {|#0:ex.Message|}));");

    [Fact]
    public Task TypedLogger_Flagged() => Flagged(@"_typed.LogError(""x {M}"", {|#0:ex.Message|});");

    [Fact]
    public Task DirectLogCall_Flagged() => Flagged(@"_log.Log(LogLevel.Information, ex, {|#0:ex.Message|}, (s, e) => s);");

    [Fact]
    public Task ExtensionBeginScope_Flagged() => Flagged(@"_log.BeginScope(""x {M}"", {|#0:ex.Message|});");

    [Fact]
    public Task InterfaceBeginScope_Flagged() => Flagged(@"_log.BeginScope<string>({|#0:ex.Message|});");

    [Fact]
    public Task LoggerMessageMethod_Flagged() => Flagged(@"Log.Failed(_log, {|#0:ex.Message|});");

    [Fact]
    public Task LoggerMessageDefineDelegate_Flagged() => Flagged(@"Defined(_log, {|#0:ex.Message|}, ex);");

    [Fact]
    public Task BareExceptionAsParamsArgument_Flagged() => Flagged(@"_log.LogError(""x {E}"", {|#0:ex|});");

    [Fact]
    public Task BareExceptionInInterpolation_Flagged() => Flagged(@"_log.LogWarning($""x { {|#0:ex|} }"");");

    [Fact]
    public Task BareExceptionInConcatenation_Flagged() => Flagged(@"_log.LogWarning(""x "" + {|#0:ex|});");

    [Fact]
    public Task TwoHitsInOneCall_ReportsBoth() =>
        Flagged(@"_log.LogError(""{A} {B}"", {|#0:ex.Message|}, {|#1:ex.ToString()|});", count: 2);

    [Fact]
    public Task ExceptionObjectAsExceptionArgument_Clean() => Clean(@"_log.LogError(ex, ""x"");");

    [Fact]
    public Task InnerExceptionObjectAsExceptionArgument_Clean() => Clean(@"_log.LogError(ex.InnerException, ""x"");");

    [Fact]
    public Task ExceptionTypeName_Clean() => Clean(@"_log.LogError(ex, ""x {T}"", ex.GetType().Name);");

    [Fact]
    public Task ConditionalAccessTypeName_Clean() => Clean(@"_log.LogError(""x {T}"", ex.InnerException?.GetType().Name);");

    [Fact]
    public Task ConditionalAccessMessage_Flagged() => Flagged(@"_log.LogError(""x {M}"", ex.InnerException?{|#0:.Message|});");

    [Fact]
    public Task ExceptionHResultAndStackTrace_Clean() => Clean(@"_log.LogError(""x {H} {S}"", ex.HResult, ex.StackTrace);");

    [Fact]
    public Task MessageOfNonException_Clean() => Clean(@"_log.LogError(""x {M}"", new Other().Message);");

    [Fact]
    public Task MessageOutsideLogging_Clean() => Clean(@"var s = ex.Message; System.GC.KeepAlive(s);");

    [Fact]
    public Task NonLoggerLogError_Clean() => Clean(@"new FakeLogger().LogError(""x"", ex.Message);");

    // Known gap, documented in THEMIA105.md: no dataflow, so a message copied to a local first is not seen.
    [Fact]
    public Task MessageCopiedToLocalFirst_Clean() => Clean(@"var m = ex.Message; _log.LogError(""x {M}"", m);");

    [Fact]
    public Task MessageInsideFormatterLambda_Clean() => Clean(@"_log.Log(LogLevel.Information, ex, ""s"", (s, e) => ex.Message);");

    [Fact]
    public Task Suppressed_Clean() => Clean(@"
#pragma warning disable THEMIA105
            _log.LogError(""x {M}"", ex.Message);
#pragma warning restore THEMIA105
");
}
