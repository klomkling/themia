using Microsoft.Extensions.Logging;

namespace Themia.Mediator.Tests.TestDoubles;

// THEMIA012 fires for any accessible open-generic type when [assembly: GenerateMediatorHandlers]
// is present. NullTestLogger<T> is a test helper, not a handler. Suppress the generator diagnostic.
#pragma warning disable THEMIA012
/// <summary>
/// No-op <see cref="ILogger{T}"/> for unit tests. No assertions are made on log output.
/// </summary>
internal sealed class NullTestLogger<T> : ILogger<T>
{
    public static readonly NullTestLogger<T> Instance = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
#pragma warning restore THEMIA012
